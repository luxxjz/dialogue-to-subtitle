# check-tree.ps1 - 用 manifests/tree.txt 核对项目目录是否完整
# 用法: powershell -ExecutionPolicy Bypass -File scripts\check-tree.ps1
#       -Strict  大小也必须一致(默认只检查是否缺失)
# 退出码: 0 = 必需项齐全, 1 = 有缺失
param([switch]$Strict)

$ErrorActionPreference = 'Stop'
# 不改 OutputEncoding: 与 deploy.ps1 保持一致, 让中文按控制台本身的代码页输出

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root      = Split-Path -Parent $ScriptDir
$manPath   = Join-Path $Root 'manifests\tree.txt'
if (-not (Test-Path $manPath)) { Write-Host "缺少 manifests\tree.txt, 先跑 scripts\gen-tree.ps1" -ForegroundColor Red; exit 1 }

$rows = @()
foreach ($line in (Get-Content $manPath -Encoding UTF8)) {
    if ($line.StartsWith('#') -or $line.Trim() -eq '') { continue }
    $p = $line.Split("`t")
    if ($p.Count -lt 4) { continue }
    $rows += [pscustomobject]@{
        Rel  = $p[0]
        Size = [long]$p[1]
        Src  = $p[2]
        Must = $p[3]
    }
}

$hard = @('必需', '仓库', '构建')

function Format-Size2([long]$b) {
    if ($b -ge 1GB) { return ('{0:N2} GB' -f ($b / 1GB)) }
    if ($b -ge 1MB) { return ('{0:N1} MB' -f ($b / 1MB)) }
    if ($b -ge 1KB) { return ('{0:N1} KB' -f ($b / 1KB)) }
    return ('{0} B' -f $b)
}

$missingHard = @()
$missingSoft = @()
$sizeDiff    = @()
$okCount     = 0

foreach ($r in $rows) {
    $full = Join-Path $Root ($r.Rel -replace '/', '\')
    if (-not (Test-Path $full)) {
        if ($hard -contains $r.Must) { $missingHard += $r } else { $missingSoft += $r }
        continue
    }
    $len = (Get-Item $full).Length
    if ($len -eq 0 -and $hard -contains $r.Must) {
        $missingHard += $r
        continue
    }
    if ($r.Size -gt 0 -and $len -ne $r.Size) { $sizeDiff += [pscustomobject]@{ R = $r; Actual = $len } }
    $okCount++
}

# 特例: whisper 模型二选一(默认 medium, -SmallModel 装 small), 缺一个不算失败
$wMed = 'models/ggml-medium.bin'; $wSml = 'models/ggml-small.bin'
$missRel = @($missingHard | ForEach-Object { $_.Rel })
if (($missRel -contains $wMed) -and -not ($missRel -contains $wSml)) {
    $missingHard = @($missingHard | Where-Object { $_.Rel -ne $wMed })
    $missingSoft += [pscustomobject]@{ Rel = ($wMed + '  (已用 small 替代)'); Size = 0; Src = 'hf'; Must = '可选' }
} elseif (($missRel -contains $wSml) -and -not ($missRel -contains $wMed)) {
    $missingHard = @($missingHard | Where-Object { $_.Rel -ne $wSml })
}

# 磁盘上多出来的文件(不在清单里)
$known = @{}
foreach ($r in $rows) { $known[$r.Rel] = $true }
$extra = @()
Get-ChildItem -Path $Root -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($Root.Length + 1).Replace('\', '/')
    $top = ($rel -split '/')[0]
    if ($top -in @('.git', '.cache', '.vs', 'obj', 'bin', '_transub_tmp')) { return }
    if (-not $known.ContainsKey($rel)) { $extra += $rel }
}

Write-Host ''
Write-Host ('文件树核对  ({0} 条记录, 已就位 {1})' -f $rows.Count, $okCount) -ForegroundColor Cyan
Write-Host ('-' * 64) -ForegroundColor DarkCyan

if ($missingHard.Count -gt 0) {
    Write-Host ('缺少必需文件 {0} 个:' -f $missingHard.Count) -ForegroundColor Red
    foreach ($m in $missingHard | Sort-Object Rel) {
        Write-Host ('  [缺] {0,-52} {1}' -f $m.Rel, $m.Src) -ForegroundColor Red
    }
} else {
    Write-Host '必需文件: 全部就位' -ForegroundColor Green
}

# 按来源汇总可选缺失, 避免刷屏
if ($missingSoft.Count -gt 0) {
    Write-Host ''
    Write-Host ('可选文件未安装 {0} 个 (不影响运行):' -f $missingSoft.Count) -ForegroundColor Yellow
    $missingSoft | Group-Object Src | ForEach-Object {
        Write-Host ('  {0,-16} {1} 个' -f $_.Name, $_.Count) -ForegroundColor DarkYellow
    }
}

if ($sizeDiff.Count -gt 0) {
    $show = if ($Strict) { $sizeDiff } else { @() }
    if ($Strict) {
        Write-Host ''
        Write-Host ('大小不一致 {0} 个:' -f $show.Count) -ForegroundColor Yellow
        foreach ($d in $show | Select-Object -First 20) {
            Write-Host ('  {0,-52} 清单 {1} / 实际 {2}' -f $d.R.Rel, (Format-Size2 $d.R.Size), (Format-Size2 $d.Actual)) -ForegroundColor Yellow
        }
    } else {
        Write-Host ''
        Write-Host ('{0} 个文件大小与清单不同 (改了源码或换了模型版本时正常, 加 -Strict 查看明细)' -f $sizeDiff.Count) -ForegroundColor DarkGray
    }
}

if ($extra.Count -gt 0) {
    Write-Host ''
    Write-Host ('清单外文件 {0} 个:' -f $extra.Count) -ForegroundColor DarkGray
    foreach ($e in $extra | Sort-Object | Select-Object -First 15) { Write-Host ('  {0}' -f $e) -ForegroundColor DarkGray }
    if ($extra.Count -gt 15) { Write-Host ('  … 另有 {0} 个' -f ($extra.Count - 15)) -ForegroundColor DarkGray }
}

Write-Host ''
if ($missingHard.Count -gt 0) {
    Write-Host '结论: 不完整, 跑 deploy.ps1 补齐' -ForegroundColor Red
    exit 1
} else {
    Write-Host '结论: 完整可用' -ForegroundColor Green
    exit 0
}
