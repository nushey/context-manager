param(
    [Alias('y')]
    [switch]$Yes
)

& {
    $ErrorActionPreference = 'Stop'

    $dotnetUrl = 'https://dotnet.microsoft.com/download/dotnet/10'
    $hasSdk = $false
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $hasSdk = @(dotnet --list-sdks | Where-Object { [int]($_.Split('.')[0]) -ge 10 }).Count -gt 0
    }
    if (-not $hasSdk) {
        throw "ContextManager requires the .NET 10 SDK or later. Install it from $dotnetUrl and re-run this installer."
    }

    $installed = dotnet tool list -g | Select-Object -Skip 2 | Where-Object { ($_ -split '\s+')[0] -eq 'contextmanager' }
    if ($installed) {
        dotnet tool update -g ContextManager
    } else {
        dotnet tool install -g ContextManager
    }

    $tool = Join-Path $env:USERPROFILE '.dotnet\tools\context-manager.exe'
    if ($Yes) {
        & $tool setup -y
    } else {
        & $tool setup
    }
    if ($LASTEXITCODE -ne 0) {
        throw "context-manager setup failed with exit code $LASTEXITCODE."
    }
}
