# gen-tree.ps1 - 扫描项目目录, 生成 FILES.md(完整文件树) 与 manifests/tree.txt(部署清单)
# 用法: powershell -ExecutionPolicy Bypass -File scripts\gen-tree.ps1
#      -WriteManifest 时同时刷新 manifests/tree.txt
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root      = Split-Path -Parent $ScriptDir

# ------------------------------------------------------------
# 来源分类规则: 顺序匹配, 先命中者生效
#   Src  = 该文件从哪来
#   Must = 缺了程序能用吗
# ------------------------------------------------------------
$Rules = @(
    # --- 本机私有(不进仓库) ---
    @{ P = 'config.txt';                  Src = 'local';   Must = '需自建' }
    @{ P = '本地ollama.txt';              Src = 'local';   Must = '可选'   }

    # --- 生成物/文档 ---
    @{ P = 'FILES.md';                    Src = 'doc';     Must = '文档' }
    @{ P = 'manifests/*';                 Src = 'doc';     Must = '文档' }
    @{ P = 'scripts/*';                   Src = 'repo';    Must = '仓库' }

    # --- 仓库自带 ---
    @{ P = '.gitattributes';              Src = 'repo';    Must = '仓库' }
    @{ P = '.gitignore';                  Src = 'repo';    Must = '仓库' }
    @{ P = 'LICENSE';                     Src = 'repo';    Must = '仓库' }
    @{ P = 'README.md';                   Src = 'repo';    Must = '仓库' }
    @{ P = 'README.txt';                  Src = 'repo';    Must = '仓库' }
    @{ P = 'ATTRIBUTIONS.md';             Src = 'repo';    Must = '仓库' }
    @{ P = 'config.example.txt';          Src = 'repo';    Must = '仓库' }
    @{ P = 'deploy.ps1';                  Src = 'repo';    Must = '仓库' }
    @{ P = 'modelfile/*';                 Src = 'repo';    Must = '仓库' }
    @{ P = 'gui/build.ps1';               Src = 'repo';    Must = '仓库' }
    @{ P = 'gui/_icon/srt.ico';           Src = 'repo';    Must = '仓库' }
    @{ P = 'gui/_icon/gen_icons.cs';      Src = 'repo';    Must = '仓库' }
    @{ P = 'gui/_icon/gen_icons.exe';     Src = 'build';   Must = '可选' }
    @{ P = 'gui/*.cs';                    Src = 'repo';    Must = '仓库' }
    @{ P = 'tools/README.md';             Src = 'repo';    Must = '仓库' }
    @{ P = 'tools/note.txt';              Src = 'repo';    Must = '仓库' }
    @{ P = 'tools/TSCharacters.txt';      Src = 'repo';    Must = '必需' }
    @{ P = 'tools/ollama/qwen35mt.jinja'; Src = 'repo';    Must = '可选' }

    # --- VC++ 运行库(取自 System32) ---
    @{ P = 'tools/lib/msvcp140*.dll';     Src = 'vc';      Must = '必需' }
    @{ P = 'tools/lib/vcruntime140*.dll'; Src = 'vc';      Must = '必需' }

    # --- 托管 DLL(NuGet) ---
    @{ P = 'tools/lib/*.dll';             Src = 'nuget';   Must = '必需' }

    # --- 第三方运行时 ---
    @{ P = 'tools/ffmpeg/presets/*';      Src = 'upstream'; Must = '可选' }
    @{ P = 'tools/ffmpeg/README.txt';     Src = 'upstream'; Must = '可选' }
    @{ P = 'tools/ffmpeg/*';              Src = 'ffmpeg';  Must = '必需' }
    @{ P = 'tools/ollama/*';              Src = 'ollama';  Must = '必需' }
    @{ P = 'tools/whisper-vulkan-new/*';  Src = 'whisper'; Must = '必需' }
    @{ P = 'tools/whisper-vulkan/*';      Src = 'legacy';  Must = '可选' }

    # --- 模型 ---
    @{ P = 'models/ollama/metadata/*';    Src = 'ollama-create'; Must = '可选' }
    @{ P = 'models/ollama/*';             Src = 'ollama-create'; Must = '必需' }
    @{ P = 'models/ggml-vad.bin';         Src = 'junk';    Must = '可删' }
    @{ P = 'models/*';                    Src = 'hf';      Must = '必需' }

    # --- 编译产物 ---
    @{ P = 'srt_gui.exe';                 Src = 'build';   Must = '构建' }
    @{ P = '*.dll';                       Src = 'build';   Must = '构建' }
)

$SrcName = @{
    'repo'          = '仓库自带'
    'doc'           = '仓库自带'
    'nuget'         = 'NuGet 包'
    'vc'            = 'System32'
    'ffmpeg'        = 'BtbN FFmpeg'
    'ollama'        = 'ollama 安装包'
    'whisper'       = '本仓库 Release'
    'legacy'        = '旧版(不还原)'
    'upstream'      = '上游包附带'
    'hf'            = '模型站点'
    'ollama-create' = 'ollama create'
    'build'         = 'build.ps1'
    'local'         = '本机私有'
    'junk'          = '残留(可删)'
}

# ------------------------------------------------------------
# 扫描
# ------------------------------------------------------------
$skipDirs = @('.git', '.cache', '.vs', 'obj', 'bin', '_transub_tmp')
$files = New-Object System.Collections.Generic.List[object]

Get-ChildItem -Path $Root -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($Root.Length + 1).Replace('\', '/')
    # 只看顶层目录名, 否则 tools/ffmpeg/bin 会被误判成构建输出 bin/
    $top = ($rel -split '/')[0]
    if ($skipDirs -contains $top) { return }
    $src = 'local'; $must = '?'
    foreach ($r in $Rules) {
        if ($rel -like $r.P) { $src = $r.Src; $must = $r.Must; break }
    }
    $files.Add([pscustomobject]@{
        Rel  = $rel
        Size = $_.Length
        Src  = $src
        Must = $must
    })
}
$files = @($files | Sort-Object Rel)

function Format-Size2([long]$b) {
    if ($b -ge 1GB) { return ('{0:N2} GB' -f ($b / 1GB)) }
    if ($b -ge 1MB) { return ('{0:N1} MB' -f ($b / 1MB)) }
    if ($b -ge 1KB) { return ('{0:N1} KB' -f ($b / 1KB)) }
    return ('{0} B' -f $b)
}

# ------------------------------------------------------------
# 目录树(ASCII)
# ------------------------------------------------------------
function Build-Tree {
    param([string[]]$Paths)

    $nodes = @{}
    foreach ($p in $Paths) {
        $parts = $p.Split('/')
        $cur = $nodes
        for ($i = 0; $i -lt $parts.Length; $i++) {
            $name = $parts[$i]
            if ($i -eq $parts.Length - 1) {
                $cur[$name] = $null      # 文件
            } else {
                if (-not $cur.ContainsKey($name)) { $cur[$name] = @{} }
                $cur = $cur[$name]
            }
        }
    }

    $sb = New-Object System.Text.StringBuilder
    function Walk {
        param($node, [string]$prefix)
        $keys = @($node.Keys | Sort-Object)
        $dirs = @($keys | Where-Object { $node[$_] -ne $null })
        $leaf = @($keys | Where-Object { $node[$_] -eq $null })
        $all  = @($dirs) + @($leaf)
        for ($i = 0; $i -lt $all.Count; $i++) {
            $k     = $all[$i]
            $last  = ($i -eq $all.Count - 1)
            $branch = if ($last) { '└── ' } else { '├── ' }
            $isDir = ($node[$k] -ne $null)
            if ($isDir) {
                $n = (Get-Count $node[$k])
                [void]$sb.AppendLine(('{0}{1}{2}/  ({3} 个文件)' -f $prefix, $branch, $k, $n))
                $next = if ($last) { $prefix + '    ' } else { $prefix + '│   ' }
                Walk $node[$k] $next
            } else {
                [void]$sb.AppendLine(('{0}{1}{2}' -f $prefix, $branch, $k))
            }
        }
    }
    function Get-Count {
        param($node)
        $c = 0
        foreach ($k in $node.Keys) {
            if ($node[$k] -eq $null) { $c++ } else { $c += (Get-Count $node[$k]) }
        }
        return $c
    }

    [void]$sb.AppendLine('SRTproject/')
    Walk $nodes ''
    return $sb.ToString().TrimEnd()
}

# ------------------------------------------------------------
# 生成 FILES.md
# ------------------------------------------------------------
$total     = ($files | Measure-Object Size -Sum).Sum
# 按"显示名"聚合: repo 与 doc 都显示为"仓库自带", 否则会出现两行同名
$bySrc = $files |
    Group-Object { $SrcName[$_.Src] } |
    ForEach-Object { [pscustomobject]@{ Name = $_.Name; Count = $_.Count; Size = ($_.Group | Measure-Object Size -Sum).Sum } } |
    Sort-Object Size -Descending

$groups = @(
    @{ Key = '仓库自带'; Title = '一、仓库自带（clone 即有，无需下载）'; Srcs = @('repo') }
    @{ Key = '文档';     Title = '二、文档与清单（clone 即有）';        Srcs = @('doc') }
    @{ Key = '第三方';   Title = '三、第三方运行时（deploy.ps1 下载）';  Srcs = @('ffmpeg', 'ollama', 'whisper') }
    @{ Key = '托管';     Title = '四、托管 DLL 与 VC++ 运行库';         Srcs = @('nuget', 'vc') }
    @{ Key = '模型';     Title = '五、模型文件（体积主体）';            Srcs = @('hf', 'ollama-create') }
    @{ Key = '构建';     Title = '六、构建产物（编译后出现）';          Srcs = @('build') }
    @{ Key = '冗余';     Title = '七、上游包附带 / 旧版本 / 本机私有（非必需）'; Srcs = @('legacy', 'upstream', 'local', 'junk') }
)

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# 完整文件树')
[void]$md.AppendLine('')
[void]$md.AppendLine(('安装完成后，项目目录下应当有 **{0} 个文件**，合计 **{1}**。本文件由 `scripts/gen-tree.ps1` 扫描实际目录生成，不是手写的。' -f $files.Count, (Format-Size2 $total)))
[void]$md.AppendLine('')
[void]$md.AppendLine('判定标准：拿本表逐条比对，或直接运行部署脚本自带的完整性检查。')
[void]$md.AppendLine('')
[void]$md.AppendLine('## 组成概览')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 来源 | 文件数 | 体积 | 由谁产生 |')
[void]$md.AppendLine('|---|---:|---:|---|')
foreach ($g in $bySrc) {
    $n    = $g.Count
    $sz   = $g.Size
    $desc = switch ($g.Name) {
        '仓库自带'      { 'git clone' }
        'NuGet 包'      { 'deploy.ps1 → nuget.org' }
        'System32'      { 'deploy.ps1 → 本机 System32' }
        'BtbN FFmpeg'   { 'deploy.ps1 → BtbN/FFmpeg-Builds' }
        'ollama 安装包' { 'deploy.ps1 → ollama/ollama v0.35.1' }
        '本仓库 Release' { 'deploy.ps1 → 本仓库 Release v1.0.0' }
        '旧版(不还原)'  { '本项目未使用，可不还原' }
        '上游包附带'    { '本机 ffmpeg 包自带，BtbN 包无此文件' }
        '模型站点'      { 'deploy.ps1 → HuggingFace / whisper.cpp' }
        'ollama create' { 'deploy.ps1 → ollama create' }
        'build.ps1'     { 'gui/build.ps1 编译' }
        '本机私有'      { '你自己创建' }
        '残留(可删)'    { '建议删除' }
        default         { '' }
    }
    [void]$md.AppendLine(('| {0} | {1} | {2} | {3} |' -f $g.Name, $n, (Format-Size2 $sz), $desc))
}
[void]$md.AppendLine(('| **合计** | **{0}** | **{1}** | |' -f $files.Count, (Format-Size2 $total)))
[void]$md.AppendLine('')

[void]$md.AppendLine('## 目录树')
[void]$md.AppendLine('')
[void]$md.AppendLine('```')
[void]$md.AppendLine((Build-Tree ($files | ForEach-Object { $_.Rel })))
[void]$md.AppendLine('```')
[void]$md.AppendLine('')

$idx = 0
foreach ($grp in $groups) {
    $idx++
    $rows = @($files | Where-Object { $grp.Srcs -contains $_.Src })
    if ($rows.Count -eq 0) { continue }
    $sum = ($rows | Measure-Object Size -Sum).Sum
    [void]$md.AppendLine(('## {0}' -f $grp.Title))
    [void]$md.AppendLine('')
    [void]$md.AppendLine(('共 {0} 个文件，{1}。' -f $rows.Count, (Format-Size2 $sum)))
    [void]$md.AppendLine('')
    [void]$md.AppendLine('| 路径 | 大小 | 来源 | 必需性 |')
    [void]$md.AppendLine('|---|---:|---|---|')
    foreach ($r in $rows) {
        $size = if ($r.Rel -eq 'FILES.md') { '—' } else { Format-Size2 $r.Size }
        [void]$md.AppendLine(('| `{0}` | {1} | {2} | {3} |' -f $r.Rel, $size, $SrcName[$r.Src], $r.Must))
    }
    [void]$md.AppendLine('')
}

# 尾部说明
[void]$md.AppendLine('## 怎么核对')
[void]$md.AppendLine('')
[void]$md.AppendLine('```powershell')
[void]$md.AppendLine('# 逐项比对当前目录与清单(缺必需文件时退出码 1)')
[void]$md.AppendLine('powershell -ExecutionPolicy Bypass -File .\scripts\check-tree.ps1')
[void]$md.AppendLine('')
[void]$md.AppendLine('# 连文件大小一起比对(改了源码/换了模型版本时会有差值, 属正常)')
[void]$md.AppendLine('powershell -ExecutionPolicy Bypass -File .\scripts\check-tree.ps1 -Strict')
[void]$md.AppendLine('')
[void]$md.AppendLine('# 文件有增减时重新生成本表')
[void]$md.AppendLine('powershell -ExecutionPolicy Bypass -File .\scripts\gen-tree.ps1')
[void]$md.AppendLine('```')
[void]$md.AppendLine('')
[void]$md.AppendLine('机读版本是 `manifests/tree.txt`（`路径<TAB>字节数<TAB>来源<TAB>必需性`），每行一个文件，可以直接喂给脚本或 CI。')
[void]$md.AppendLine('')
[void]$md.AppendLine('## 说明')
[void]$md.AppendLine('')
[void]$md.AppendLine('- **`tools/whisper-vulkan/` 与 `tools/whisper-vulkan-new/`**：程序按 -new → 旧版 顺序查找，`whisper-vulkan-new` 是当前使用的那份。旧目录保留是因为原作者同时留了，deploy.ps1 不还原它，缺了不影响运行。')
[void]$md.AppendLine('- **`models/ggml-vad.bin`** 只有 15 字节，内容是 `Entry not found`，是早先一次失败下载的残留。代码里没有任何地方读它（VAD 用的是 `ggml-silero-v6.2.0.bin`），可以删。')
[void]$md.AppendLine('- **根目录那 11 个 DLL**（DirectML / onnxruntime / MathNet / System.Memory / System.Runtime.CompilerServices.Unsafe / onnxruntime_providers_shared / msvcp140 / msvcp140_1 / vcruntime140 / vcruntime140_1 / Microsoft.ML.OnnxRuntime）不是下载来的，是 `gui/build.ps1` 每次编译时从 `tools/lib/` 复制到 exe 同级的。换机重编译会自动重新生成。')
[void]$md.AppendLine('- **`tools/ffmpeg/LICENSE`** 由 deploy.ps1 从压缩包里的 `LICENSE*` 写入（统一改名）。**`presets/*.ffpreset` 和 `README.txt`** 是你本机装的 gyan.dev 完整包自带的，deploy.ps1 用的 BtbN 包不含这两项；脚本发现压缩包里有 `presets/` 才会铺过去。程序没有调用 `-pre` 预设，README 只是构建说明，缺了都不影响运行。')
[void]$md.AppendLine('- **`tools/ollama/qwen35mt.jinja`** 是内置翻译模型模板的副本（内容与 `modelfile/qwen3.5-mt-2b.Modelfile` 的 TEMPLATE 块一致），ollama 构建时不读它，保留仅作参考。')
[void]$md.AppendLine('- **`config.txt`** 含你的片源路径，不进仓库；clone 后从 `config.example.txt` 复制一份改名即可。')
[void]$md.AppendLine('- **本表按默认安装记录**。用 `-SmallModel` 时 `models/ggml-medium.bin` 会换成 `ggml-small.bin`；whisper 回退到 CPU 构建时 `tools/whisper-vulkan-new/` 里的文件集合也可能与上表不同（少 `ggml-vulkan.dll`）。这两种情况 `check-tree.ps1` 都不会判为失败。')
[void]$md.AppendLine('- **根目录 DLL 与 `srt_gui.exe`** 每次编译都会被覆盖，大小会随源码变动；`-Strict` 模式下的体积差异属正常。')
[void]$md.AppendLine('')

# 只写有变化时
$mdPath = Join-Path $Root 'FILES.md'
$utf8 = New-Object Text.UTF8Encoding $false
$newMd = $md.ToString()
$oldMd = if (Test-Path $mdPath) { [IO.File]::ReadAllText($mdPath, [Text.Encoding]::UTF8) } else { '' }
if ($newMd -ne $oldMd) {
    [IO.File]::WriteAllText($mdPath, $newMd, $utf8)
    Write-Host ('已更新 FILES.md ({0} 个文件)' -f $files.Count)
} else {
    Write-Host 'FILES.md 无变化'
}

# ------------------------------------------------------------
# 生成 manifests/tree.txt (部署清单, 不含文档)
# ------------------------------------------------------------
$manDir = Join-Path $Root 'manifests'
if (-not (Test-Path $manDir)) { New-Item -ItemType Directory -Path $manDir -Force | Out-Null }

$deployable = @($files)
$m = New-Object System.Text.StringBuilder
[void]$m.AppendLine('# 部署清单: 路径<TAB>字节数<TAB>来源<TAB>必需性')
[void]$m.AppendLine('# 由 scripts/gen-tree.ps1 生成; scripts/check-tree.ps1 用它核对实际目录')
[void]$m.AppendLine('# 必需性: 必需/仓库/构建 = 缺了就是问题; 文档/可选/需自建/可删 = 不计入失败')
foreach ($r in $deployable) {
    $sz = if ($r.Src -eq 'doc') { 0 } else { $r.Size }   # 本脚本会重写这两个文件, 大小不稳定
    [void]$m.AppendLine(('{0}' -f $r.Rel) + "`t" + $sz + "`t" + $r.Src + "`t" + $r.Must)
}
$manPath = Join-Path $manDir 'tree.txt'
[IO.File]::WriteAllText($manPath, $m.ToString(), $utf8)
Write-Host ('已更新 manifests/tree.txt ({0} 条)' -f $deployable.Count)
