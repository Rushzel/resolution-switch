Push-Location $PSScriptRoot
try {
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /out:ResolutionSwitch.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll ResolutionSwitch.cs
    if ($LASTEXITCODE -eq 0) { .\ResolutionSwitch.exe --self-test | Write-Output }
}
finally {
    Pop-Location
}
exit $LASTEXITCODE
