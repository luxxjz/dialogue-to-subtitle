# build.ps1 - 一键编译 srt_gui.exe (转 UTF-8 BOM + csc 编译)
# 用法: powershell -ExecutionPolicy Bypass -File gui\build.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path   # gui\
$proj = Split-Path -Parent $root                          # SRTproject 根

# 源码必须是 UTF-8 带 BOM, 否则 csc 按 ANSI 读中文会乱码
Get-ChildItem "$root\*.cs" | ForEach-Object {
    $c = [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText($_.FullName, $c, (New-Object System.Text.UTF8Encoding $true))
}

$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
& "$fw\csc.exe" /nologo /target:winexe /win32icon:"$root\_icon\srt.ico" /out:"$proj\srt_gui.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Numerics.dll `
    /r:"$fw\netstandard.dll" `
    /r:"$fw\System.Web.Extensions.dll" `
    /r:"$proj\tools\lib\System.Memory.dll" `
    /r:"$proj\tools\lib\Microsoft.ML.OnnxRuntime.dll" /r:"$proj\tools\lib\MathNet.Numerics.dll" `
    "$root\Program.cs" "$root\MainForm.cs" "$root\Config.cs" "$root\Pipeline.cs" "$root\ProcRunner.cs" "$root\MDXSep.cs" `
    "$root\SrtUtil.cs" "$root\Recognize.cs" "$root\LlmPost.cs" "$root\LlmServer.cs" "$root\VkUtil.cs"
if ($LASTEXITCODE -ne 0) { Write-Host "编译失败"; exit 1 }

# 运行时 DLL 拷贝到 exe 目录 (托管 + DML 原生 onnxruntime + MathNet + System.Memory + Unsafe + providers_shared + DirectML)
Copy-Item "$proj\tools\lib\Microsoft.ML.OnnxRuntime.dll"  "$proj\" -Force
Copy-Item "$proj\tools\lib\MathNet.Numerics.dll"          "$proj\" -Force
Copy-Item "$proj\tools\lib\onnxruntime.dll"               "$proj\" -Force
Copy-Item "$proj\tools\lib\System.Memory.dll"             "$proj\" -Force
Copy-Item "$proj\tools\lib\System.Runtime.CompilerServices.Unsafe.dll" "$proj\" -Force
Copy-Item "$proj\tools\lib\onnxruntime_providers_shared.dll"          "$proj\" -Force
Copy-Item "$proj\tools\lib\DirectML.dll"                  "$proj\" -Force
# VC++ 运行库 (目标机可能未装, 内置保证单目录即用)
Copy-Item "$proj\tools\lib\msvcp140.dll"                  "$proj\" -Force
Copy-Item "$proj\tools\lib\msvcp140_1.dll"                "$proj\" -Force
Copy-Item "$proj\tools\lib\vcruntime140.dll"              "$proj\" -Force
Copy-Item "$proj\tools\lib\vcruntime140_1.dll"            "$proj\" -Force
Write-Host "编译完成: $proj\srt_gui.exe"
