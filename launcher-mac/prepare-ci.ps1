param([string]$Repository = 'D:/claudetest/DustoreMacCI')
$ErrorActionPreference = 'Stop'
$taskMacSource = [IO.Path]::GetFullPath($PSScriptRoot)
$taskCiRoot = [IO.Path]::GetFullPath($Repository)
if (!(Test-Path -LiteralPath (Join-Path $taskCiRoot '.git'))) { throw 'Expected the existing authorized Git checkout.' }
$taskMacMirror = Join-Path $taskCiRoot 'launcher-mac'
$taskMirrorPrefix = [IO.Path]::GetFullPath($taskMacMirror).TrimEnd('\') + '\'
$taskManifest = @()
foreach ($taskSourceFile in Get-ChildItem -LiteralPath $taskMacSource -File -Recurse -Force) {
    # Windows PowerShell 5.1 has no Path.GetRelativePath; every file is under the source root.
    $taskRelative = $taskSourceFile.FullName.Substring($taskMacSource.TrimEnd('\').Length + 1)
    $taskSegments = $taskRelative.Split([char[]]'\/')
    if ($taskSegments[0] -in @('bin', 'obj', 'artifacts', 'publish', 'delivery', '.github', '.git') -or $taskSourceFile.Name -in @('download-artifact.py', 'validate-release.py') -or $taskSegments -contains '__pycache__' -or $taskSourceFile.Extension -eq '.pyc') { continue }
    $taskDestination = [IO.Path]::GetFullPath((Join-Path $taskMacMirror $taskRelative))
    if (!$taskDestination.StartsWith($taskMirrorPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'A mirror file escaped the new launcher directory.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskDestination)) | Out-Null
    [IO.File]::Copy($taskSourceFile.FullName, $taskDestination, $true)
    if ($taskSourceFile.Extension -in @('.cs', '.csproj', '.axaml', '.yml', '.py', '.ps1', '.md', '.entitlements')) {
        $taskText = [IO.File]::ReadAllText($taskDestination).Replace("`r`n", "`n")
        [IO.File]::WriteAllText($taskDestination, $taskText, [Text.UTF8Encoding]::new($false))
    }
    $taskManifest += [ordered]@{ path = $taskRelative.Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $taskDestination -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$taskWorkflowSource = Join-Path $taskMacSource '.github/workflows/macos-launcher.yml'
$taskWorkflowDestination = Join-Path $taskCiRoot '.github/workflows/macos-launcher.yml'
[IO.File]::Copy($taskWorkflowSource, $taskWorkflowDestination, $true)
$taskProjectXml = [xml][IO.File]::ReadAllText((Join-Path $taskMacSource 'DustoreLauncherV.Mac.csproj'))
[ordered]@{ product = 'DUSTORE LAUNCHER V macOS'; version = [string]$taskProjectXml.Project.PropertyGroup.Version; files = $taskManifest; workflowSha256 = (Get-FileHash -LiteralPath $taskWorkflowSource -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskMacMirror 'source-manifest.json') -Encoding utf8
$taskAttributesPath = Join-Path $taskCiRoot '.gitattributes'
$taskAttributes = [IO.File]::ReadAllText($taskAttributesPath)
foreach ($taskAttribute in @('*.axaml text eol=lf', '*.icns binary', '*.png binary')) {
    if (!$taskAttributes.Contains($taskAttribute)) { $taskAttributes += "`n" + $taskAttribute }
}
[IO.File]::WriteAllText($taskAttributesPath, $taskAttributes.TrimEnd() + "`n", [Text.UTF8Encoding]::new($false))
[ordered]@{ mirror = $taskMacMirror; sourceFiles = $taskManifest.Count; workflow = $taskWorkflowDestination; excluded = @('Build outputs', 'Test profiles', 'Original games', 'Python caches') } | ConvertTo-Json
