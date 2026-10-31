#Requires -Version 5.1
<#
    SRT 自动字幕生成 —— 一键依赖部署脚本
    ------------------------------------------------------------------
    把运行本程序所需的全部第三方依赖(ffmpeg / whisper / ollama / 各类模型 / 托管 DLL)
    从上游下载下来, 还原成程序期望的目录结构, 最后编译出 srt_gui.exe。

    特性:
      · 断点续传 + 进度显示, 中断后重跑会接着下
      · 国内网络自动回退到 hf-mirror 镜像
      · 幂等: 已经就位的组件自动跳过, 可反复运行
      · 分组件跳过: 只缺模型时不用重下 ffmpeg

    示例:
      powershell -ExecutionPolicy Bypass -File .\deploy.ps1
      powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -Mirror cn
      powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -SkipLlm -SmallModel
      powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -Only models,llm
      powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -GgufPath D:\models\my-qwen.gguf
#>
[CmdletBinding()]
param(
    # auto = 先官方源,失败自动切 hf-mirror; global = 只用官方; cn = 优先镜像
    [ValidateSet('auto', 'global', 'cn')]
    [string]$Mirror = 'auto',

    # 只部署这些组件(可组合): lib ffmpeg ollama whisper models llm build
    [string[]]$Only = @(),

    # 跳过这些组件
    [string[]]$Skip = @(),

    # 跳过内置 LLM 模型(省 1.9 GB, 只影响"内嵌引擎"这一档)
    [switch]$SkipLlm,

    # 用 ggml-small 代替 ggml-medium (省 1 GB, 识别精度略降)
    [switch]$SmallModel,

    # 用本地已有的 GGUF 建内置模型, 不下载基座
    [string]$GgufPath = '',

    # 强制重下(忽略已存在的文件)
    [switch]$Force,

    # 只下依赖, 不编译
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
# 不改控制台编码: 脚本本身是 UTF-8 BOM, PowerShell 会正确解码中文;
# 若强改 OutputEncoding 为 UTF-8, 在默认 GBK 的 cmd 窗口里反而会乱码。
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ==================================================================
#  常量 —— 换仓库/换版本改这里
# ==================================================================
$Script:RepoSlug   = 'luxxjz/dialogue-to-subtitle'      # GitHub 仓库, 用于自编译的 whisper Vulkan 包
$Script:ReleaseTag = 'v1.0.0'

$Script:Root  = Split-Path -Parent $PSCommandPath
$Script:Cache = Join-Path $Script:Root '.cache'
$Script:Tmp   = Join-Path $Script:Cache 'extract'

# 版本固定, 保证可复现
$VerOllama    = 'v0.35.1'
$VerWhisper   = 'b5454'      # whisper.cpp 官方 CPU 构建所用 tag
$VerOnnx      = '1.24.4'
$VerMathNet   = '4.15.0'
$VerMemory    = '4.5.5'
$VerUnsafe    = '4.5.3'
$VerGguf      = 'Qwen3.5-2B-Q4_K_M.gguf'

# 内置翻译模型: ollama 官方注册表 kaelri/qwen3.5-mt:2b 的同一份 GGUF
# (blob 摘要 = 文件 sha256, 与作者发布的模型逐字节一致)
$MtgRepo      = 'kaelri/qwen3.5-mt'
$MtgDigest    = '4ecac2ba3fd000cf4638ce65dbffacbd8062c2c87a24a75559af421341adb4b2'
$MtgSize      = 1945311744
$VerMtgGuf    = 'Qwen3.5-MT-2B-Q4_K_M.gguf'

$StepTotal = 0
$StepIndex = 0

# ==================================================================
#  输出工具
# ==================================================================
function Format-Size {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:0.0} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:0.0} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:0} KB' -f ($Bytes / 1KB)) }
    return ('{0} B' -f [int]$Bytes)
}

function Write-Step {
    param([string]$Text)
    $Script:StepIndex++
    Write-Host ''
    Write-Host ('[{0}/{1}] {2}' -f $Script:StepIndex, $Script:StepTotal, $Text) -ForegroundColor Cyan
    Write-Host ('-' * 64) -ForegroundColor DarkCyan
}

function Write-Ok   { param([string]$Text) Write-Host "  [OK]   $Text" -ForegroundColor Green }
function Write-Skip { param([string]$Text) Write-Host "  [跳过] $Text" -ForegroundColor DarkGray }
function Write-Note { param([string]$Text) Write-Host "  [提示] $Text" -ForegroundColor Yellow }
function Write-Fail { param([string]$Text) Write-Host "  [失败] $Text" -ForegroundColor Red }

# ==================================================================
#  端口 / 本地服务探测
# ==================================================================
function Test-PortOpen {
    param([int]$Port)
    $c = New-Object Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne(300)) { return $false }
        $c.EndConnect($iar)
        return $true
    } catch {
        return $false
    } finally {
        $c.Close()
    }
}

function Test-OllamaReady {
    param([int]$Port)
    try {
        $req = [Net.HttpWebRequest]::Create(('http://127.0.0.1:{0}/api/tags' -f $Port))
        $req.Proxy = $null          # 本地回环不走系统代理
        $req.Timeout = 3000
        $resp = $req.GetResponse()
        $code = [int]$resp.StatusCode
        $resp.Close()
        return ($code -eq 200)
    } catch {
        return $false
    }
}

# ==================================================================
#  下载(.NET 实现: 断点续传 + 进度)
# ==================================================================
function Get-HfBaseList {
    switch ($Mirror) {
        'cn'     { return @('https://hf-mirror.com') }
        'global' { return @('https://huggingface.co') }
        default  { return @('https://huggingface.co', 'https://hf-mirror.com') }
    }
}

function Invoke-HttpDownload {
    param([string]$Url, [string]$Dest, [long]$MinBytes)

    # 是否存在/是否完整 由 Get-Download 判断; 这里只管下载(有半截就续传)
    if ($Force -and (Test-Path $Dest)) { Remove-Item $Dest -Force -ErrorAction SilentlyContinue }

    $dir = Split-Path -Parent $Dest
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $start = 0
    if ((Test-Path $Dest) -and -not $Force) { $start = (Get-Item $Dest).Length }

    $req = [Net.HttpWebRequest]::Create($Url)
    $req.UserAgent = 'SRTproject-deploy/1.0 (+https://github.com/)'
    $req.Timeout = 60000
    $req.ReadWriteTimeout = 600000
    $req.AllowAutoRedirect = $true
    if ($start -gt 0) { $req.AddRange($start) }

    $resp = $req.GetResponse()
    $respLen = $resp.ContentLength
    $resumed = $false
    if ($start -gt 0 -and $resp.StatusCode -eq [Net.HttpStatusCode]::OK) {
        # 服务器不支持 Range, 从头来
        $start = 0
    } elseif ($start -gt 0) {
        $resumed = $true
    }
    $total = $respLen + $start

    $mode = [IO.FileMode]::Create
    if ($resumed) { $mode = [IO.FileMode]::Append }
    $fs = [IO.File]::Open($Dest, $mode, [IO.FileAccess]::Write)
    $stream = $resp.GetResponseStream()
    $buf = New-Object byte[] (1MB)
    $got = $start
    $lastPct = -1
    try {
        while ($true) {
            $n = $stream.Read($buf, 0, $buf.Length)
            if ($n -le 0) { break }
            $fs.Write($buf, 0, $n)
            $got += $n
            if ($total -gt 0) {
                $pct = [int](($got * 100) / $total)
                if ($pct -ne $lastPct) {
                    Write-Host -NoNewline ("`r         {0,3}%  {1} / {2}   " -f $pct, (Format-Size $got), (Format-Size $total))
                    $lastPct = $pct
                }
            } else {
                Write-Host -NoNewline ("`r         {0}   " -f (Format-Size $got))
            }
        }
        Write-Host ''
    } finally {
        $fs.Close()
        $stream.Close()
        $resp.Close()
    }

    $finalLen = 0
    if (Test-Path $Dest) { $finalLen = (Get-Item $Dest).Length }
    if ($finalLen -lt $MinBytes) {
        Remove-Item $Dest -Force -ErrorAction SilentlyContinue
        throw ('文件不完整或非预期内容 ({0} < 期望 {1})' -f (Format-Size $finalLen), (Format-Size $MinBytes))
    }
    return $true
}

function Test-RemoteSize {
    <#
      用 HEAD 取上游真实字节数, 判断本地文件是不是下完了。
      返回 $true=一致 / $false=不一致 / $null=上游没给长度(无法判断)
    #>
    param([string[]]$Urls, [string]$Dest)
    if (-not (Test-Path $Dest)) { return $false }
    $len = (Get-Item $Dest).Length
    foreach ($u in $Urls) {
        try {
            $req = [Net.HttpWebRequest]::Create($u)
            $req.Method = 'HEAD'
            $req.UserAgent = 'SRTproject-deploy/1.0'
            $req.Timeout = 15000
            $req.AllowAutoRedirect = $true
            $resp = $req.GetResponse()
            $cl = $resp.ContentLength
            $resp.Close()
            if ($cl -gt 0) { return ($len -eq $cl) }
        } catch { }
    }
    return $null
}

function Get-Download {
    param(
        [string[]]$Urls,
        [string]$Dest,
        [long]$MinBytes = 1024,
        [string]$Desc = ''
    )

    if (-not $Force -and (Test-Path $Dest) -and (Get-Item $Dest).Length -ge $MinBytes) {
        # MinBytes 只是"看着像完整文件"的下限, 真正判断要问上游
        $cmp = Test-RemoteSize -Urls $Urls -Dest $Dest
        if ($cmp -ne $false) {
            Write-Skip ('{0} 已就位 ({1})' -f (Split-Path $Dest -Leaf), (Format-Size (Get-Item $Dest).Length))
            return $true
        }
        Write-Note ('{0} 大小与上游不符, 重新下载' -f (Split-Path $Dest -Leaf))
    }

    $name = Split-Path $Dest -Leaf
    if ($Desc -ne '') { Write-Host "  $Desc" -ForegroundColor Gray }
    $errors = @()
    foreach ($u in $Urls) {
        Write-Host "  来源: $u" -ForegroundColor DarkGray
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            try {
                Invoke-HttpDownload -Url $u -Dest $Dest -MinBytes $MinBytes | Out-Null
                Write-Ok ('{0} ({1})' -f $name, (Format-Size (Get-Item $Dest).Length))
                return $true
            } catch {
                $errors += ('{0} -> {1}' -f $u, $_.Exception.Message)
                if ($attempt -lt 2 -and (Test-Path $Dest)) {
                    # 保留已下部分, 第二轮续传
                    Write-Host '         重试(续传)…' -ForegroundColor Yellow
                }
            }
        }
        if (Test-Path $Dest) { Remove-Item $Dest -Force -ErrorAction SilentlyContinue }
    }
    Write-Fail ("$name 所有来源均失败")
    foreach ($e in $errors) { Write-Host "        $e" -ForegroundColor DarkRed }
    throw "下载 $name 失败"
}

function Get-DownloadHash {
    <#
      下载并校验 sha256。用于权重这种"来源多、必须字节一致"的文件:
      逐源尝试, 每下完一个就比对摘要, 不符就换下一个源, 全部失败才抛错。
    #>
    param(
        [string[]]$Urls,
        [string]$Dest,
        [string]$Sha256,
        [long]$Size,
        [string]$Desc = ''
    )
    $Sha256 = $Sha256.ToLower()
    $name   = Split-Path $Dest -Leaf

    if (-not $Force -and (Test-Path $Dest) -and (Get-Item $Dest).Length -eq $Size) {
        $h = (Get-FileHash -Path $Dest -Algorithm SHA256).Hash.ToLower()
        if ($h -eq $Sha256) {
            Write-Skip ('{0} 已就位 (sha256 校验通过)' -f $name)
            return $true
        }
        Write-Note ('{0} 摘要不符, 重新下载' -f $name)
        Remove-Item $Dest -Force -ErrorAction SilentlyContinue
    }

    foreach ($u in $Urls) {
        try {
            Get-Download -Urls @($u) -Dest $Dest -MinBytes $Size -Desc $Desc | Out-Null
        } catch {
            continue
        }
        Write-Host '  校验 sha256…' -ForegroundColor Gray
        $h = (Get-FileHash -Path $Dest -Algorithm SHA256).Hash.ToLower()
        if ($h -eq $Sha256) {
            Write-Ok ('{0} sha256 校验通过 ({1})' -f $name, (Format-Size (Get-Item $Dest).Length))
            return $true
        }
        Write-Note ('来源内容不符 (期望 {0}…, 实得 {1}…), 换下一个源' -f $Sha256.Substring(0, 12), $h.Substring(0, 12))
        Remove-Item $Dest -Force -ErrorAction SilentlyContinue
    }
    throw ('{0} 所有来源均未通过 sha256 校验' -f $name)
}

# ==================================================================
#  解压 / 取文件
# ==================================================================
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

function Expand-Zip {
    param([string]$Zip, [string]$Dest)
    if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Dest)
}

function New-TempDir {
    param([string]$Name)
    $d = Join-Path $Script:Tmp $Name
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    return $d
}

function Copy-FirstMatch {
    param(
        [string]$FromDir,
        [string]$FileName,
        [string]$ToDir,
        [string[]]$Prefer = @()
    )
    $hits = @(Get-ChildItem -Path $FromDir -Recurse -File -Filter $FileName -ErrorAction SilentlyContinue)
    if ($hits.Count -eq 0) { return $false }
    $pick = $null
    foreach ($p in $Prefer) {
        $pick = $hits | Where-Object { $_.FullName.ToLower().Contains($p.ToLower()) } | Select-Object -First 1
        if ($pick) { break }
    }
    if (-not $pick) { $pick = $hits[0] }
    if (-not (Test-Path $ToDir)) { New-Item -ItemType Directory -Path $ToDir -Force | Out-Null }
    Copy-Item $pick.FullName (Join-Path $ToDir $FileName) -Force
    return $true
}

function Get-NuGetPackage {
    param([string]$Id, [string]$Version)
    if (-not (Test-Path $Script:Cache)) { New-Item -ItemType Directory -Path $Script:Cache -Force | Out-Null }
    $file = ('{0}.{1}.nupkg' -f $Id.ToLower(), $Version)
    $url  = ('https://api.nuget.org/v3-flatcontainer/{0}/{1}/{2}' -f $Id.ToLower(), $Version, $file)
    $dest = Join-Path $Script:Cache $file
    Get-Download -Urls @($url) -Dest $dest -MinBytes 1000 | Out-Null
    return $dest
}

# ==================================================================
#  组件 1: 托管 DLL (NuGet 还原)
# ==================================================================
function Deploy-Lib {
    Write-Step '托管 DLL (onnxruntime / MathNet / System.*)'
    $libDir = Join-Path $Script:Root 'tools\lib'
    if (-not (Test-Path $libDir)) { New-Item -ItemType Directory -Path $libDir -Force | Out-Null }

    $need = @(
        'Microsoft.ML.OnnxRuntime.dll', 'onnxruntime.dll', 'DirectML.dll',
        'onnxruntime_providers_shared.dll', 'MathNet.Numerics.dll',
        'System.Memory.dll', 'System.Runtime.CompilerServices.Unsafe.dll'
    )
    $allThere = $true
    foreach ($f in $need) { if (-not (Test-Path (Join-Path $libDir $f))) { $allThere = $false } }
    if ($allThere -and -not $Force) {
        Write-Skip 'tools\lib 7 个托管 DLL 已就位'
        return
    }

    $jobs = @(
        @{ Id = 'Microsoft.ML.OnnxRuntime.Managed'; Ver = $VerOnnx;   Files = @('Microsoft.ML.OnnxRuntime.dll'); Prefer = @('netstandard2.0') },
        @{ Id = 'Microsoft.ML.OnnxRuntime.DirectML'; Ver = $VerOnnx;  Files = @('onnxruntime.dll', 'DirectML.dll', 'onnxruntime_providers_shared.dll'); Prefer = @('win-x64', 'win7-x64') },
        @{ Id = 'MathNet.Numerics';                  Ver = $VerMathNet; Files = @('MathNet.Numerics.dll'); Prefer = @('net48', 'netstandard2.0') },
        @{ Id = 'System.Memory';                     Ver = $VerMemory; Files = @('System.Memory.dll'); Prefer = @('net461') },
        @{ Id = 'System.Runtime.CompilerServices.Unsafe'; Ver = $VerUnsafe; Files = @('System.Runtime.CompilerServices.Unsafe.dll'); Prefer = @('net461') }
    )

    foreach ($j in $jobs) {
        $pkg = Get-NuGetPackage -Id $j.Id -Version $j.Ver
        $tmp = New-TempDir $j.Id
        Expand-Zip -Zip $pkg -Dest $tmp
        foreach ($f in $j.Files) {
            if (Copy-FirstMatch -FromDir $tmp -FileName $f -ToDir $libDir -Prefer $j.Prefer) {
                Write-Ok ('{0}  ({1})' -f $f, $j.Id)
            } else {
                Write-Fail ("$($j.Id) 中未找到 $f")
            }
        }
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    # VC++ 运行库: 让程序目录自包含, 换机免装 redist
    $vc = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
    $vcFound = 0
    foreach ($f in $vc) {
        $src = Join-Path $env:SystemRoot ('System32\' + $f)
        if (Test-Path $src) {
            Copy-Item $src $libDir -Force
            $vcFound++
        }
    }
    if ($vcFound -eq $vc.Count) {
        Write-Ok 'VC++ 运行库 (4 个 DLL, 取自本机 System32)'
    } elseif ($vcFound -gt 0) {
        Write-Note 'VC++ 运行库仅取到部分文件; 目标机若缺 msvcp140/vcruntime140 请装 VC++ 2015-2022 x64'
    } else {
        Write-Note '未取到 VC++ 运行库; 如目标机报 0xc000007b, 请安装 vc_redist.x64.exe (https://aka.ms/vs/17/release/vc_redist.x64.exe)'
    }
}

# ==================================================================
#  组件 2: FFmpeg
# ==================================================================
function Deploy-Ffmpeg {
    Write-Step 'FFmpeg (音轨提取 / 切片 / 转码)'
    $binDir = Join-Path $Script:Root 'tools\ffmpeg\bin'
    $probe  = Join-Path $binDir 'ffprobe.exe'
    if (-not $Force -and (Test-Path $probe) -and (Test-Path (Join-Path $binDir 'ffmpeg.exe'))) {
        Write-Skip 'tools\ffmpeg\bin 已就位'
        return
    }

    $url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip'
    $zip = Join-Path $Script:Cache 'ffmpeg-win64-gpl.zip'
    Get-Download -Urls @($url) -Dest $zip -MinBytes 40000000 -Desc 'BtbN GPL 构建 (约 150 MB)' | Out-Null

    $tmp = New-TempDir 'ffmpeg'
    Expand-Zip -Zip $zip -Dest $tmp
    $okAll = $true
    foreach ($exe in @('ffmpeg.exe', 'ffprobe.exe', 'ffplay.exe')) {
        if (Copy-FirstMatch -FromDir $tmp -FileName $exe -ToDir $binDir -Prefer @('bin')) {
            Write-Ok $exe
        } else {
            Write-Fail "压缩包中未找到 $exe"
            $okAll = $false
        }
    }
    # 附带许可文件 (统一命名为 LICENSE, 与完整文件树一致)
    $lic = Get-ChildItem -Path $tmp -Recurse -File -Filter 'LICENSE*' | Select-Object -First 1
    if ($lic) { Copy-Item $lic.FullName (Join-Path $Script:Root 'tools\ffmpeg\LICENSE') -Force }
    # 上游若带 presets/ (gyan.dev 等完整包), 一并铺过去; BtbN 包没有, 跳过即可
    $pre = Get-ChildItem -Path $tmp -Recurse -Directory -Filter 'presets' | Select-Object -First 1
    if ($pre) {
        $dstPre = Join-Path $Script:Root 'tools\ffmpeg\presets'
        if (-not (Test-Path $dstPre)) { New-Item -ItemType Directory -Path $dstPre -Force | Out-Null }
        Copy-Item (Join-Path $pre.FullName '*') $dstPre -Force
    }

    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if ($okAll) {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Write-Note '注意: FFmpeg 为 GPL v3, 再分发本项目+FFmpeg 时需遵守其许可'
    }
}

# ==================================================================
#  组件 3: Ollama 引擎
# ==================================================================
function Deploy-Ollama {
    Write-Step ('Ollama 推理引擎 ({0})' -f $VerOllama)
    $dir = Join-Path $Script:Root 'tools\ollama'
    $exe = Join-Path $dir 'ollama.exe'
    if (-not $Force -and (Test-Path $exe)) {
        Write-Skip 'tools\ollama\ollama.exe 已就位'
        return
    }

    $url = ('https://github.com/ollama/ollama/releases/download/{0}/ollama-windows-amd64.zip' -f $VerOllama)
    $zip = Join-Path $Script:Cache ('ollama-' + $VerOllama + '.zip')
    Get-Download -Urls @($url) -Dest $zip -MinBytes 200000000 -Desc 'Windows amd64 版 (约 1.5 GB)' | Out-Null

    $tmp = New-TempDir 'ollama'
    Expand-Zip -Zip $zip -Dest $tmp
    $found = Get-ChildItem -Path $tmp -Recurse -File -Filter 'ollama.exe' | Select-Object -First 1
    if (-not $found) { throw '压缩包中未找到 ollama.exe' }
    $srcRoot = $found.Directory.FullName
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    # 整个目录结构搬过去 (ollama.exe + lib\ollama\*)
    Get-ChildItem -Path $srcRoot -Force | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $dir $_.Name) -Recurse -Force
    }
    Write-Ok 'tools\ollama (ollama.exe + lib)'

    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
}

# ==================================================================
#  组件 4: whisper-cli (Vulkan GPU)
# ==================================================================
function Deploy-Whisper {
    Write-Step 'whisper.cpp 识别引擎 (Vulkan GPU)'
    $dir = Join-Path $Script:Root 'tools\whisper-vulkan-new'
    $exe = Join-Path $dir 'whisper-cli.exe'
    if (-not $Force -and (Test-Path $exe)) {
        Write-Skip 'tools\whisper-vulkan-new\whisper-cli.exe 已就位'
        return
    }

    $gpuOk = $false
    if ($Script:RepoSlug -notmatch 'OWNER') {
        $selfUrl = ('https://github.com/{0}/releases/download/{1}/whisper-vulkan-win-x64.zip' -f $Script:RepoSlug, $Script:ReleaseTag)
        $zip = Join-Path $Script:Cache 'whisper-vulkan-win-x64.zip'
        try {
            Get-Download -Urls @($selfUrl) -Dest $zip -MinBytes 5000000 -Desc '本仓库 Release 附件 (自编译 Vulkan 构建, 约 17 MB)' | Out-Null
            $tmp = New-TempDir 'whisper'
            Expand-Zip -Zip $zip -Dest $tmp
            $n = 0
            Get-ChildItem -Path $tmp -Recurse -File | Where-Object { $_.Extension -in @('.exe', '.dll') } | ForEach-Object {
                if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
                Copy-Item $_.FullName (Join-Path $dir $_.Name) -Force
                $n++
            }
            Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item $zip -Force -ErrorAction SilentlyContinue
            if ($n -gt 0) { Write-Ok ("Vulkan 构建 ($n 个文件, GPU 加速可用)"); $gpuOk = $true }
        } catch {
            Write-Note 'Release 附件不可用, 回退到官方 CPU 构建'
        }
    }

    if (-not $gpuOk) {
        $cpuUrl = ('https://github.com/ggml-org/whisper.cpp/releases/download/{0}/whisper-bin-x64.zip' -f $VerWhisper)
        $zip = Join-Path $Script:Cache ('whisper-cpu-' + $VerWhisper + '.zip')
        Get-Download -Urls @($cpuUrl) -Dest $zip -MinBytes 1000000 -Desc 'whisper.cpp 官方 CPU 构建' | Out-Null
        $tmp = New-TempDir 'whisper-cpu'
        Expand-Zip -Zip $zip -Dest $tmp
        Get-ChildItem -Path $tmp -Recurse -File | Where-Object { $_.Extension -in @('.exe', '.dll') } | ForEach-Object {
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Copy-Item $_.FullName (Join-Path $dir $_.Name) -Force
        }
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Write-Ok 'CPU 构建已就位'
        Write-Note '未取得 Vulkan 版, 识别将走 CPU (较慢)。想要 GPU 加速见 tools/README.md 自行编译'
    }
}

# ==================================================================
#  组件 5: 模型文件
# ==================================================================
function Deploy-Models {
    Write-Step '模型文件 (whisper / VAD / 人声分离)'
    $mdir = Join-Path $Script:Root 'models'
    if (-not (Test-Path $mdir)) { New-Item -ItemType Directory -Path $mdir -Force | Out-Null }
    $hf = Get-HfBaseList

    # --- whisper 识别模型 ---
    if ($SmallModel) {
        $wName = 'ggml-small.bin'; $wMin = 400000000
    } else {
        $wName = 'ggml-medium.bin'; $wMin = 1400000000
    }
    $urls = @()
    foreach ($b in $hf) { $urls += ('{0}/ggerganov/whisper.cpp/resolve/main/{1}' -f $b, $wName) }
    Get-Download -Urls $urls -Dest (Join-Path $mdir $wName) -MinBytes $wMin -Desc ("Whisper 模型 {0}" -f $wName) | Out-Null

    # --- Silero VAD ---
    $vadName = 'ggml-silero-v6.2.0.bin'
    $urls = @()
    foreach ($b in $hf) {
        $urls += ('{0}/ggml-org/whisper-vad/resolve/main/{1}' -f $b, $vadName)
        $urls += ('{0}/ggerganov/whisper.cpp/resolve/main/{1}' -f $b, $vadName)
    }
    Get-Download -Urls $urls -Dest (Join-Path $mdir $vadName) -MinBytes 500000 -Desc 'Silero VAD (~0.9 MB)' | Out-Null

    # --- 人声分离 onnx ---
    $sepModels = @('UVR-MDX-NET-Inst_HQ_3.onnx', 'Kim_Vocal_2.onnx')
    foreach ($m in $sepModels) {
        $urls = @(
            ('https://github.com/nomadkaraoke/python-audio-separator/releases/download/model-configs/{0}' -f $m),
            ('https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/{0}' -f $m)
        )
        Get-Download -Urls $urls -Dest (Join-Path $mdir $m) -MinBytes 40000000 -Desc ("人声分离模型 {0} (~64 MB)" -f $m) | Out-Null
    }
}

# ==================================================================
#  组件 6: 内置 LLM (ollama 自定义模型)
# ==================================================================
function Deploy-Llm {
    Write-Step '内置翻译模型 qwen3.5-mt:2b'
    $modelsDir = Join-Path $Script:Root 'models\ollama'
    $manifest  = Join-Path $modelsDir 'manifests\registry.ollama.ai\library\qwen3.5-mt\2b'

    if (-not $Force -and (Test-Path $manifest)) {
        Write-Skip 'models\ollama 中已有 qwen3.5-mt:2b'
        return
    }

    $ollamaExe = Join-Path $Script:Root 'tools\ollama\ollama.exe'
    if (-not (Test-Path $ollamaExe)) { throw '缺少 tools\ollama\ollama.exe, 请先部署 ollama 组件' }

    $gguf = $GgufPath
    if ($gguf -ne '') {
        if (-not (Test-Path $gguf)) { throw "指定的 GGUF 不存在: $gguf" }
        $gguf = (Resolve-Path $gguf).Path
        Write-Ok ('使用本地 GGUF: ' + $gguf)
    } else {
        # 首选: ollama 官方注册表里 kaelri/qwen3.5-mt:2b 的 GGUF 层
        # 这一层就是作者发布的权重本身, sha256 与程序内置模型逐字节一致
        $ggufDir = Join-Path $Script:Cache 'gguf'
        $gguf    = Join-Path $ggufDir $VerMtgGuf
        $fromOfficial = $true
        try {
            Get-DownloadHash `
                -Urls @(('https://registry.ollama.ai/v2/{0}/blobs/sha256:{1}' -f $MtgRepo, $MtgDigest)) `
                -Dest $gguf -Sha256 $MtgDigest -Size $MtgSize `
                -Desc ('Qwen3.5-MT-2B Q4_K_M 官方权重 (~1.9 GB)') | Out-Null
        } catch {
            $fromOfficial = $false
            Write-Note '官方注册表取不到, 回退到 HuggingFace 通用基座'
        }

        if (-not $fromOfficial) {
            # 兜底: 通用 Qwen3.5-2B (非翻译微调版), 配合仓库内模板仍可翻译, 但效果不等同作者版
            $gguf = Join-Path $ggufDir $VerGguf
            $hf = Get-HfBaseList
            $urls = @()
            foreach ($b in $hf) {
                $urls += ('{0}/unsloth/Qwen3.5-2B-GGUF/resolve/main/{1}' -f $b, $VerGguf)
                $urls += ('{0}/prithivMLmods/Qwen3.5-2B-MTP-GGUF/resolve/main/Qwen3.5-2B.Q4_K_M.gguf' -f $b)
            }
            Get-Download -Urls $urls -Dest $gguf -MinBytes 1000000000 -Desc 'Qwen3.5-2B Q4_K_M 通用基座 (~1.3 GB)' | Out-Null
            Write-Note '当前为通用基座, 如需与作者一致的翻译质量请改用官方权重'
        }
    }

    # 用仓库里的 Modelfile 模板, 把 FROM 换成本地 gguf
    $mfSrc = Join-Path $Script:Root 'modelfile\qwen3.5-mt-2b.Modelfile'
    if (-not (Test-Path $mfSrc)) { throw "缺少 modelfile\qwen3.5-mt-2b.Modelfile" }
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($l in (Get-Content -Path $mfSrc -Encoding UTF8)) {
        if ($l.TrimStart().StartsWith('#')) { continue }           # 注释不参与构建
        if ($lines.Count -eq 0 -and $l.Trim() -eq '') { continue }  # 跳过文件头部空行
        $lines.Add($l)                                              # 块内空行必须保留
    }
    while ($lines.Count -gt 1 -and $lines[$lines.Count - 1].Trim() -eq '') { $lines.RemoveAt($lines.Count - 1) }
    $lines[0] = ('FROM "{0}"' -f ($gguf -replace '\\', '/'))
    $tmpMf = Join-Path $Script:Cache 'qwen3.5-mt-2b.build.Modelfile'
    if (-not (Test-Path $Script:Cache)) { New-Item -ItemType Directory -Path $Script:Cache -Force | Out-Null }
    # 必须用 LF: 模板/系统提示的原版 blob 就是 LF, 用 CRLF 会生成不同摘要(内容等价但不再逐字节一致)
    [IO.File]::WriteAllText($tmpMf, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))

    if (-not (Test-Path $modelsDir)) { New-Item -ItemType Directory -Path $modelsDir -Force | Out-Null }
    Write-Host '  构建模型(需要 1-3 分钟, 期间无输出属正常)…' -ForegroundColor Gray

    # 用一个独立端口的临时 ollama 服务来构建:
    # 若本机已装 Ollama 且在运行(常见), 直接调 CLI 会把模型建到它的模型库里, 而不是项目的 models\ollama
    $oldModels = $env:OLLAMA_MODELS
    $oldHost   = $env:OLLAMA_HOST
    $port = 11500
    while (Test-PortOpen $port) { $port++ }
    $env:OLLAMA_MODELS = $modelsDir
    $env:OLLAMA_HOST   = ('127.0.0.1:{0}' -f $port)

    $pidSrv = 0
    try {
        # 不要用 Start-Process: PowerShell 5.1 在"环境变量仅大小写重复"(如同时存在
        # HTTPS_PROXY 与 https_proxy)时会抛"已添加项", 且 -UseNewEnvironment 也绕不过。
        # 走 WMI 创建进程, 环境变量在 cmd 里现设, 顺带把输出重定向到日志。
        $log = Join-Path $Script:Cache 'ollama-serve.log'
        $cmdLine = ('cmd /c set "OLLAMA_MODELS={0}" && set "OLLAMA_HOST=127.0.0.1:{1}" && "{2}" serve > "{3}" 2>&1' -f `
                    $modelsDir, $port, $ollamaExe, $log)
        $pidSrv = 0
        try {
            $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmdLine } -ErrorAction Stop
            if ($r -and $r.ReturnValue -eq 0) { $pidSrv = [int]$r.ProcessId }
        } catch {
            $pidSrv = 0
        }

        $ready = $false
        if ($pidSrv -gt 0) {
            for ($i = 0; $i -lt 60; $i++) {
                Start-Sleep -Milliseconds 500
                if (Test-OllamaReady $port) { $ready = $true; break }
                if (-not (Get-Process -Id $pidSrv -ErrorAction SilentlyContinue)) { break }
            }
        }

        if ($ready) {
            & $ollamaExe create 'qwen3.5-mt:2b' -f $tmpMf
        } else {
            # 临时服务起不来(权限/端口/WMI 限制), 退回直接调 CLI
            Write-Note '临时服务未就绪, 改用本机 Ollama CLI 构建'
            & $ollamaExe create 'qwen3.5-mt:2b' -f $tmpMf
        }
        if ($LASTEXITCODE -ne 0) { throw ("ollama create 失败 (退出码 $LASTEXITCODE)") }
    } finally {
        if ($pidSrv -gt 0) {
            # 只关本次启动的 ollama serve(连同它的 cmd 父进程)
            Get-CimInstance Win32_Process -Filter ("ParentProcessId={0}" -f $pidSrv) -ErrorAction SilentlyContinue |
                ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
            Stop-Process -Id $pidSrv -Force -ErrorAction SilentlyContinue
        }
        $env:OLLAMA_MODELS = $oldModels
        $env:OLLAMA_HOST   = $oldHost
    }

    if (-not (Test-Path $manifest)) {
        throw ('模型没有写进 models\ollama：本机很可能有另一个 Ollama 在运行(托盘程序/ollama serve)，' +
               'create 请求被它接走了。请退出 Ollama 后重跑本脚本，或手动执行：' +
               '$env:OLLAMA_MODELS="<项目>\models\ollama"; .\tools\ollama\ollama.exe create qwen3.5-mt:2b -f .\modelfile\qwen3.5-mt-2b.Modelfile')
    }
    Write-Ok 'qwen3.5-mt:2b 已写入 models\ollama'

    # 权重已进 blobs, 删掉临时 gguf 省 1.9 GB
    if ($GgufPath -eq '' -and (Test-Path $gguf)) {
        Remove-Item $gguf -Force -ErrorAction SilentlyContinue
        Write-Note '临时 GGUF 已清理(模型已复制进 models\ollama\blobs)'
    }
}

# ==================================================================
#  组件 7: 编译
# ==================================================================
function Invoke-Build {
    Write-Step '编译 srt_gui.exe'
    $buildPs1 = Join-Path $Script:Root 'gui\build.ps1'
    if (-not (Test-Path $buildPs1)) { throw '缺少 gui\build.ps1' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $buildPs1
    if ($LASTEXITCODE -ne 0) { throw '编译失败, 见上方输出' }
    $exe = Join-Path $Script:Root 'srt_gui.exe'
    if (Test-Path $exe) {
        Write-Ok ('srt_gui.exe ({0})' -f (Format-Size (Get-Item $exe).Length))
    } else {
        throw '编译脚本已执行但未生成 srt_gui.exe'
    }
}

# ==================================================================
#  依赖检查
# ==================================================================
function Invoke-Verify {
    Write-Host ''
    Write-Host '依赖完整性检查' -ForegroundColor Cyan
    Write-Host ('-' * 64) -ForegroundColor DarkCyan
    $checks = @(
        @{ Path = 'srt_gui.exe';                                   Desc = '主程序' },
        @{ Path = 'tools\ffmpeg\bin\ffmpeg.exe';                   Desc = 'ffmpeg' },
        @{ Path = 'tools\ffmpeg\bin\ffprobe.exe';                  Desc = 'ffprobe' },
        @{ Path = 'tools\whisper-vulkan-new\whisper-cli.exe';      Desc = 'whisper-cli' },
        @{ Path = 'tools\lib\onnxruntime.dll';                     Desc = 'onnxruntime' },
        @{ Path = 'tools\lib\DirectML.dll';                        Desc = 'DirectML' },
        @{ Path = 'tools\lib\MathNet.Numerics.dll';                Desc = 'MathNet' },
        @{ Path = 'tools\TSCharacters.txt';                        Desc = '繁简映射表' },
        @{ Path = 'models\ggml-silero-v6.2.0.bin';                 Desc = 'VAD 模型' },
        @{ Path = 'models\UVR-MDX-NET-Inst_HQ_3.onnx';             Desc = '人声分离模型' },
        @{ Path = 'tools\ollama\ollama.exe';                       Desc = 'ollama 引擎' },
        @{ Path = 'models\ollama\manifests\registry.ollama.ai\library\qwen3.5-mt\2b'; Desc = '内置翻译模型' }
    )
    if ($SmallModel) {
        $checks += @{ Path = 'models\ggml-small.bin'; Desc = 'whisper 模型(小)' }
    } else {
        $checks += @{ Path = 'models\ggml-medium.bin'; Desc = 'whisper 模型(中)' }
    }

    $missing = @()
    foreach ($c in $checks) {
        $full = Join-Path $Script:Root $c.Path
        if (Test-Path $full) {
            $sz = ''
            if (-not (Test-Path $full -PathType Container)) { $sz = '  ' + (Format-Size (Get-Item $full).Length) }
            Write-Host ('  [ok]   {0,-22} {1}{2}' -f $c.Desc, $c.Path, $sz) -ForegroundColor Green
        } else {
            Write-Host ('  [MISS] {0,-22} {1}' -f $c.Desc, $c.Path) -ForegroundColor Red
            $missing += $c.Desc
        }
    }

    if ($missing.Count -gt 0) {
        Write-Host ''
        Write-Note ('缺少 {0} 项: {1}' -f $missing.Count, ($missing -join ', '))
        Write-Note '可重跑本脚本补齐; 若某项始终失败, 检查网络或改用 -Mirror cn'
        return $false
    }
    return $true
}

# ==================================================================
#  主流程
# ==================================================================
$AllSteps = @('lib', 'ffmpeg', 'ollama', 'whisper', 'models', 'llm', 'build')

$run = $AllSteps
if ($Only.Count -gt 0) { $run = $run | Where-Object { $Only -contains $_ } }
if ($Skip.Count -gt 0) { $run = $run | Where-Object { $Skip -notcontains $_ } }
if ($SkipLlm)          { $run = $run | Where-Object { $_ -ne 'llm' } }
if ($NoBuild)          { $run = $run | Where-Object { $_ -ne 'build' } }

# 依赖顺序: build 必须在最后
$ordered = @()
foreach ($s in $AllSteps) { if ($run -contains $s) { $ordered += $s } }
$run = $ordered
$StepTotal = $run.Count

Write-Host ''
Write-Host '================================================================' -ForegroundColor Cyan
Write-Host '  SRT 自动字幕生成 —— 依赖一键部署' -ForegroundColor Cyan
Write-Host '================================================================' -ForegroundColor Cyan
Write-Host ('  项目目录: {0}' -f $Script:Root)
Write-Host ('  下载镜像: {0}' -f $Mirror)
Write-Host ('  本次组件: {0}' -f ($run -join ', '))
if ($GgufPath -ne '') { Write-Host ('  本地 GGUF: {0}' -f $GgufPath) }

$sw = [Diagnostics.Stopwatch]::StartNew()
$failed = $false
try {
    foreach ($step in $run) {
        switch ($step) {
            'lib'     { Deploy-Lib }
            'ffmpeg'  { Deploy-Ffmpeg }
            'ollama'  { Deploy-Ollama }
            'whisper' { Deploy-Whisper }
            'models'  { Deploy-Models }
            'llm'     { Deploy-Llm }
            'build'   { Invoke-Build }
        }
    }
} catch {
    $failed = $true
    Write-Host ''
    Write-Fail $_.Exception.Message
    Write-Host ('  位置: {0}' -f $_.InvocationInfo.PositionMessage) -ForegroundColor DarkGray
}
$sw.Stop()

$ok = Invoke-Verify

# 有清单时再做一遍逐文件核对(比上面 13 项更细, 覆盖第三方包内的全部文件)
# 仅作参考, 不改变退出码: -SmallModel / CPU 回退等选项会让清单与实际情况自然有差异
# 同进程执行(不另起 powershell), 避免子进程与父进程输出编码不一致导致中文乱码
$treeScript = Join-Path $Script:Root 'scripts\check-tree.ps1'
$treeManifest = Join-Path $Script:Root 'manifests\tree.txt'
if ((Test-Path $treeScript) -and (Test-Path $treeManifest)) {
    & $treeScript
}

Write-Host ''
Write-Host '================================================================' -ForegroundColor Cyan
if ($failed) {
    Write-Host ('  部署中断 (耗时 {0:0.0} 分钟)' -f $sw.Elapsed.TotalMinutes) -ForegroundColor Red
    Write-Host '  已完成的组件无需重下, 直接重跑本脚本即可继续。' -ForegroundColor Yellow
    exit 1
} elseif ($ok) {
    Write-Host ('  部署完成 (耗时 {0:0.0} 分钟)' -f $sw.Elapsed.TotalMinutes) -ForegroundColor Green
    Write-Host '  下一步: 直接运行 srt_gui.exe' -ForegroundColor Green
    Write-Host '  (可选) 把 config.example.txt 复制成 config.txt 做个性化配置' -ForegroundColor DarkGray
    exit 0
} else {
    Write-Host ('  部署结束但有缺项 (耗时 {0:0.0} 分钟)' -f $sw.Elapsed.TotalMinutes) -ForegroundColor Yellow
    exit 2
}
