param(
    [ValidateSet("win-x64", "win-arm64")]
    [string] $Runtime = "win-x64",
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]*$')]
    [string] $ReleaseTag
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    $releaseName = if ($ReleaseTag) { "$Runtime-$ReleaseTag" } else { $Runtime }
    $output = Join-Path $PSScriptRoot "artifacts\publish\$releaseName"
    $zip = Join-Path $PSScriptRoot "artifacts\SystemAudioRecorder-$releaseName.zip"
    if ((Test-Path $output) -or (Test-Path $zip)) {
        throw "Release output already exists. Choose a new -ReleaseTag or move the existing output yourself. Existing releases are never overwritten."
    }

    $architecture = $Runtime.Substring(4)
    dotnet test SystemAudioRecorder.slnx -c Release --arch $architecture --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; no release was published." }

    dotnet publish src\SystemAudioRecorder\SystemAudioRecorder.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    $config = Get-Content (Join-Path $output "SystemAudioRecorder.runtimeconfig.json") -Raw | ConvertFrom-Json
    $assets = Get-Content "src\SystemAudioRecorder\obj\project.assets.json" -Raw | ConvertFrom-Json
    foreach ($framework in $config.runtimeOptions.includedFrameworks) {
        $package = "$($framework.name.ToLowerInvariant()).runtime.$Runtime"
        $pack = $assets.packageFolders.PSObject.Properties.Name |
            ForEach-Object { Join-Path $_ "$package\$($framework.version)" } |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if (!$pack) { throw "Cannot find redistribution notices for $package." }
        $notices = Get-ChildItem $pack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' }
        if (!$notices) { throw "Runtime package $package contains no license notices." }
        foreach ($notice in $notices) {
            Copy-Item $notice.FullName (Join-Path $output "$($framework.name)-$($notice.Name)")
        }
    }

    foreach ($name in @("LICENSE", "THIRD-PARTY-NOTICES.txt", "README.md")) {
        if (!(Test-Path (Join-Path $output $name) -PathType Leaf)) {
            throw "Release is missing required documentation: $name"
        }
    }
    $sensitive = Get-ChildItem $output -Recurse -File | Where-Object {
        $_.Extension.ToLowerInvariant() -in @(".pcm", ".wav", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".aiff",
            ".log", ".jsonl", ".dmp", ".dump", ".pfx", ".p12", ".pem", ".key") -or
        $_.Name -eq "session-state.json" -or $_.Name -eq "recorder.lock" -or $_.Name -like ".env*"
    }
    if ($sensitive) { throw "Local audio, diagnostics, or credential files were found in publish output; archive creation was refused." }
    Compress-Archive -Path "$output\*" -DestinationPath $zip
    Get-FileHash $zip -Algorithm SHA256
    Write-Host "Run: $output\SystemAudioRecorder.exe"
}
finally {
    Pop-Location
}
