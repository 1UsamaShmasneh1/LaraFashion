# Local acceptance only. -AllowDownloads is explicit consent to the documented preparation downloads.
[CmdletBinding()]
param(
    [switch]$AllowDownloads,
    [string]$BaseImage = 'mcr.microsoft.com/dotnet/sdk:9.0.314-noble',
    [string]$PreparedImage,
    [string]$RunId = ('lf-linux-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)),
    [switch]$KeepResources
)
$ErrorActionPreference = 'Stop'
if ($RunId -notmatch '^lf-linux-[a-zA-Z0-9-]+$') { throw 'Invalid task RunId' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskRoot = Join-Path ([IO.Path]::GetTempPath()) $RunId
if (Test-Path -LiteralPath $taskRoot) { throw 'Use a fresh RunId; existing evidence must be preserved' }
New-Item -ItemType Directory -Path $taskRoot | Out-Null
$contextRoot = Join-Path $taskRoot 'input'
$sourceRoot = Join-Path $contextRoot 'source'
New-Item -ItemType Directory -Path $sourceRoot | Out-Null
$docker = (Get-Command docker -ErrorAction Stop).Source
function Invoke-Docker {
    [CmdletBinding()]
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = @(& $docker @Arguments 2>&1)
    $exit = $LASTEXITCODE
    $ErrorActionPreference = $previousPreference
    $output | Add-Content -LiteralPath (Join-Path $taskRoot 'docker.log')
    if ($exit -ne 0) { throw ('Docker failed: ' + ($Arguments -join ' ') + '; see docker.log') }
    return ($output -join "`n")
}
if ($env:DOCKER_HOST) { throw 'Explicit DOCKER_HOST refused; no remote engine is contacted' }
$contextName = (Invoke-Docker @('context','show')).Trim()
$context = @(ConvertFrom-Json (Invoke-Docker @('context','inspect',$contextName)))[0]
if ($context.Endpoints.docker.Host -ne 'npipe:////./pipe/dockerDesktopLinuxEngine') { throw 'Only the local Docker Desktop Linux pipe is accepted' }
$version = ConvertFrom-Json (Invoke-Docker @('version','--format','{{json .}}'))
if ($version.Server.Os -ne 'linux' -or $version.Server.Arch -ne 'amd64') { throw 'linux/amd64 engine required; settings were not changed' }
Invoke-Docker @('info','--format','{{json .}}') | Set-Content (Join-Path $taskRoot 'engine-private.json')
Invoke-Docker @('system','df') | Set-Content (Join-Path $taskRoot 'docker-space.log')
Invoke-Docker @('ps','-a','--format','{{.ID}} {{.Names}} {{.State}}') | Set-Content (Join-Path $taskRoot 'existing-resources.log')
$head = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw 'Git HEAD unavailable' }
$branch = (& git -C $repo branch --show-current).Trim()
$status = @(& git -C $repo status --short)
# Reviewed source extensions only. No databases/configuration/keys/uploads/Git or Windows binaries.
$files = @()
foreach ($folder in @('LaraFashion','LaraFashion.IntegrationTests')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $repo $folder) -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($repo.Length + 1).Replace('\','/')
        $relative -notmatch '(^|/)(bin|obj|\.vs|uploads|images)(/|$)' -and
        $_.Extension -in @('.cs','.csproj','.razor','.css','.js')
    }
}
$files += Get-ChildItem -LiteralPath $repo -File | Where-Object { $_.Name -match '^(Directory\.Build\.(props|targets)|global\.json|LaraFashion\.slnx|\.gitattributes)$' }
$files += Get-Item -LiteralPath (Join-Path $repo '.config/dotnet-tools.json'),(Join-Path $repo '.github/workflows/deploy.yml'),(Join-Path $repo 'deploy/deploy-production.sh')
$files += Get-Item -LiteralPath (Join-Path $repo 'deploy/first-transition.py'),(Join-Path $repo 'deploy/actions-ssh.sh'),(Join-Path $repo 'deploy/actions-remote.sh'),(Join-Path $repo 'deploy/actions-preflight.sh')
$files += Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in @('.py','.sh','.ps1') }
$files = @($files | Sort-Object FullName -Unique)
$manifest = @()
foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked source input refused' }
    $relative = $file.FullName.Substring($repo.Length + 1).Replace('\','/')
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($file.Extension -eq '.sh' -and $bytes.Contains([byte]13)) { throw ('Bash input has CR line endings: ' + $relative) }
    $before = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $destination = Join-Path $sourceRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    [IO.File]::WriteAllBytes($destination,$bytes)
    if ($before -ne (Get-FileHash -LiteralPath $destination).Hash -or $before -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'Source changed during staging' }
    $manifest += [ordered]@{Path=$relative;Sha256=$before;Classification='current-working-tree'}
}
$nuget = '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText((Join-Path $sourceRoot 'NuGet.Config'),$nuget,[Text.UTF8Encoding]::new($false))
$manifest += [ordered]@{Path='NuGet.Config';Sha256=(Get-FileHash (Join-Path $sourceRoot 'NuGet.Config')).Hash;Classification='generated-public-source-only'}
$inputMetadata = [ordered]@{ContractVersion=1;RunId=$RunId;Head=$head;Branch=$branch;Status=$status;Files=$manifest;
    Excluded=@('databases/sidecars/backups','keys/certificates/.env','real appsettings','Data Protection keys','.git/.vs/bin/obj','uploads/images','production evidence/logs')}
$inputMetadata | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $contextRoot 'manifest.json') -Encoding utf8
$sourceMetadata = [ordered]@{Head=$head;Files=$manifest}
$sourceMetadata | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $contextRoot 'source-fingerprint.json') -Encoding utf8
$fingerprint = (Get-FileHash (Join-Path $contextRoot 'source-fingerprint.json')).Hash
$resources = [Collections.Generic.List[object]]::new()
$containerName = $RunId + '-test'
$imageName = 'larafashion-linux-acceptance:' + $RunId
$containerCreated = $false
$imageCreated = $false
try {
    if (!$PreparedImage) {
        if (!$AllowDownloads) { throw 'Preparation requires explicit -AllowDownloads consent, or a matching prepared image. No download was attempted.' }
        if ($BaseImage -notmatch '^mcr\.microsoft\.com/dotnet/sdk:9\.0\.[0-9]+-noble$') { throw 'Reviewed official versioned Microsoft .NET 9 SDK required' }
        & $docker image inspect $BaseImage *> $null
        if ($LASTEXITCODE -ne 0) { Invoke-Docker @('pull','--platform','linux/amd64',$BaseImage) | Write-Host }
        $base = @(ConvertFrom-Json (Invoke-Docker @('image','inspect',$BaseImage)))[0]
        if ($base.Os -ne 'linux' -or $base.Architecture -ne 'amd64') { throw 'Base image platform mismatch' }
        $digest = @($base.RepoDigests | Where-Object { $_ -like 'mcr.microsoft.com/dotnet/sdk@sha256:*' })[0]
        if (!$digest) { throw 'Official base digest required' }
        $dockerfile = @"
FROM $digest
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
RUN apt-get update && apt-get install -y --no-install-recommends python3 psmisc procps iproute2 strace && rm -rf /var/lib/apt/lists/*
COPY source/.config/dotnet-tools.json /preparation/.config/dotnet-tools.json
COPY source/NuGet.Config /preparation/NuGet.Config
COPY source/LaraFashion/LaraFashion.csproj /preparation/LaraFashion/LaraFashion.csproj
COPY source/LaraFashion.IntegrationTests/LaraFashion.IntegrationTests.csproj /preparation/LaraFashion.IntegrationTests/LaraFashion.IntegrationTests.csproj
WORKDIR /preparation
RUN dotnet tool restore --configfile NuGet.Config && dotnet restore LaraFashion.IntegrationTests/LaraFashion.IntegrationTests.csproj --configfile NuGet.Config && dpkg-query -W > /package-versions.txt
COPY source /source
COPY manifest.json /input-manifest.json
WORKDIR /source
RUN dotnet tool restore --configfile NuGet.Config --ignore-failed-sources && dotnet restore LaraFashion.IntegrationTests/LaraFashion.IntegrationTests.csproj --configfile NuGet.Config --ignore-failed-sources
LABEL larafashion.linux.run="$RunId" larafashion.linux.manifest="$fingerprint" larafashion.linux.base="$digest"
"@
        [IO.File]::WriteAllText((Join-Path $contextRoot 'Dockerfile'),$dockerfile.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))
        Invoke-Docker @('build','--pull=false','--platform','linux/amd64','--network','default','--tag',$imageName,$contextRoot) | Write-Host
        $imageCreated = $true; $PreparedImage = $imageName
        $resources.Add([ordered]@{Kind='image';Name=$imageName;RunId=$RunId})
    }
    $image = @(ConvertFrom-Json (Invoke-Docker @('image','inspect',$PreparedImage)))[0]
    if ($image.Config.Labels.'larafashion.linux.manifest' -ne $fingerprint -or $image.Os -ne 'linux' -or $image.Architecture -ne 'amd64') { throw 'Prepared image does not match this exact staged source/platform' }
    [ordered]@{BaseDigest=$image.Config.Labels.'larafashion.linux.base';ImageId=$image.Id;SourceFingerprint=$fingerprint;ManifestSha256=(Get-FileHash (Join-Path $contextRoot 'manifest.json')).Hash;Client=$version.Client;Server=$version.Server;Context=$contextName;Endpoint=$context.Endpoints.docker.Host} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $taskRoot 'environment.json')
    Invoke-Docker @('create','--pull','never','--name',$containerName,'--label',"larafashion.linux.run=$RunId",'--network','none','--cpus','4','--memory','6g','--pids-limit','1024','--ulimit','core=0','--entrypoint','python3',$PreparedImage,'/source/deploy/tests/linux-acceptance.py','/source',$head) | Write-Host
    $containerCreated = $true; $resources.Add([ordered]@{Kind='container';Name=$containerName;RunId=$RunId})
    Invoke-Docker @('cp',(Join-Path $contextRoot 'manifest.json'),($containerName + ':/input-manifest.json')) | Write-Host
    Invoke-Docker @('start',$containerName) | Write-Host
    Write-Host "Running offline Linux tests; evidence: $taskRoot"
    # Bounded polling keeps launcher interruptible. No resource from another project is touched.
    do {
        Start-Sleep -Seconds 3
        $state = ConvertFrom-Json (Invoke-Docker @('inspect','--format','{{json .State}}',$containerName))
    } while ($state.Running)
    Invoke-Docker @('logs',$containerName) | Set-Content (Join-Path $taskRoot 'console.log')
    Invoke-Docker @('cp',($containerName + ':/evidence'),$taskRoot) | Write-Host
    Invoke-Docker @('inspect',$containerName) | Set-Content (Join-Path $taskRoot 'container-private.json')
    foreach ($row in $manifest | Where-Object Classification -eq 'current-working-tree') {
        if ($row.Sha256 -ne (Get-FileHash -LiteralPath (Join-Path $repo $row.Path)).Hash) { throw 'Source changed during execution; acceptance is invalid' }
    }
    if ($state.ExitCode -ne 0) { throw ('Linux acceptance failed, exit ' + $state.ExitCode + '; evidence preserved in ' + $taskRoot) }
    Write-Host "PASS Linux acceptance; manifest $fingerprint; evidence $taskRoot"
} finally {
    $resources | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $taskRoot 'resources.json')
    if ($containerCreated) {
        & $docker logs $containerName 2>&1 | Set-Content (Join-Path $taskRoot 'console.log')
        & $docker cp ($containerName + ':/evidence') $taskRoot 2>&1 | Add-Content (Join-Path $taskRoot 'docker.log')
        $owned = ConvertFrom-Json (Invoke-Docker @('inspect','--format','{{json .Config.Labels}}',$containerName))
        if ($owned.'larafashion.linux.run' -ne $RunId) { throw 'Cleanup ownership mismatch' }
        Invoke-Docker @('stop','--timeout','10',$containerName) | Write-Host
        if (!$KeepResources) { Invoke-Docker @('rm',$containerName) | Write-Host }
    }
    if ($imageCreated -and !$KeepResources) {
        $owned = ConvertFrom-Json (Invoke-Docker @('image','inspect','--format','{{json .Config.Labels}}',$imageName))
        if ($owned.'larafashion.linux.run' -ne $RunId) { throw 'Image cleanup ownership mismatch' }
        Invoke-Docker @('image','rm',$imageName) | Write-Host
    }
    Write-Host "Evidence retained: $taskRoot"
}
