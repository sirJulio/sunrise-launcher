Param(
    [string]$RID = "win-x64",
    [string]$Configuration = "Release",
    [switch]$Trimmed
)

# Paths
$project = Join-Path $PSScriptRoot "..\SunriseLauncher.csproj"
$outDir = Join-Path $PSScriptRoot "..\publish\$RID"

# Publish settings
$publishTrimmed = if ($Trimmed) { "true" } else { "false" }

Write-Host "Publishing project: $project"
Write-Host "RID: $RID  Configuration: $Configuration  PublishTrimmed: $publishTrimmed"
Write-Host "Output folder: $outDir"

# Ensure output directory exists
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

# Run dotnet publish
$args = @(
    $project,
    "-c", $Configuration,
    "-r", $RID,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:PublishTrimmed=$publishTrimmed",
    "-o", $outDir
)

Write-Host "Running: dotnet publish $($args -join ' ')"

$proc = Start-Process -FilePath dotnet -ArgumentList $args -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    Write-Error "dotnet publish failed with exit code $($proc.ExitCode)"
    exit $proc.ExitCode
}

Write-Host "Publish completed successfully. Files are in: $outDir"
