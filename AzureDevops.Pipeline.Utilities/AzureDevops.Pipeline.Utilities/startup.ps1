$ErrorActionPreference = 'Stop'

if (-not (Test-Path Env:AZP_URL)) {
  Write-Error "error: missing AZP_URL environment variable"
  exit 1
}
if (Test-Path Env:AZP_TOKEN)
{
  $pat = $Env:AZP_TOKEN
}
elseif (Test-Path Env:AZP_TOKEN_FILE) {
  $pat = $(Get-Content ${Env:AZP_TOKEN_FILE})
}
else {
  Write-Error "error: missing AZP_TOKEN environment variable"
  exit 1
}

if (-not $Env:AZP_WORK) {
  $Env:AZP_WORK = if($IsLinux) { [System.IO.Path]::GetFullPath('/home/azdev/agent') } else { "C:/home/azdev/agent" }
}

if (-not $Env:AZP_AGENT_DIR) {
  $Env:AZP_AGENT_DIR = if($IsLinux) { [System.IO.Path]::GetFullPath('/home/azp/agent') } else { "C:/home/azp/agent" }
}

if (Test-Path $Env:AZP_AGENT_DIR) {
  if ($Env:AZP_CLEAN_AGENT -eq "1") {
    Write-Host "Agent directory already exists. Removing it." -ForegroundColor Cyan
    Remove-Item $Env:AZP_AGENT_DIR -Recurse -Force
  }
}

New-Item $Env:AZP_AGENT_DIR -ItemType directory -Force | Out-Null

Remove-Item Env:AZP_TOKEN

if ((Test-Path Env:AZP_WORK) -and -not (Test-Path $Env:AZP_WORK)) {
  New-Item $Env:AZP_WORK -ItemType directory | Out-Null
}

$AgentBinDir = Join-Path $Env:AZP_AGENT_DIR "bin"

$csharpFile = Join-Path $PSScriptRoot "Functions.cs"

Add-Type -TypeDefinition (Get-Content -Raw -Path $csharpFile) -Language CSharp

$excludedVars = [Functions]::GetEnvironmentVars("^((GITHUB_.+)|(AZP_*))");

Write-Host "Excluded vars: $excludedVars"

# Let the agent ignore the token env variables
$Env:VSO_AGENT_IGNORE = "AZP_TOKEN,AZP_TOKEN_FILE,AZP_TASK_URL,token,taskUrl,buildNumber,pool,targetAzureRegion,image,parallelism,$Env:VSO_AGENT_IGNORE,$excludedVars"

$azureRegion = Invoke-RestMethod -Headers @{"Metadata"="true"} -Uri "http://169.254.169.254/metadata/instance/compute/location?api-version=2017-08-01&format=text"
Write-Host "Azure Region: $azureRegion"

$Env:AzureRegion = $azureRegion

Write-Host "##vso[task.setvariable variable=AzureRegion;]$azureRegion"

Set-Location $Env:AZP_AGENT_DIR

Write-Host "1. Determining matching Azure Pipelines agent..." -ForegroundColor Cyan

$archSfx = if ($IsLinux) { "tar.gz" } else { "zip" }
$os = if ($IsLinux) { "linux" } else { "win" }

$packageUrl = $Env:AZP_PACKAGE_URL
if (-not $packageUrl) {
  $packageDetailsUrl = "$(${Env:AZP_URL})/_apis/distributedtask/packages/agent?platform=$os-x64&`$top=1"

  Write-Host "Package Details Url = '$packageDetailsUrl'"

  $base64AuthInfo = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$pat"))
  $package = Invoke-RestMethod -Headers @{Authorization=("Basic $base64AuthInfo"); Accept="application/json"} $packageDetailsUrl
  $packageUrl = $package[0].Value.downloadUrl
}

Write-Host "Package Url = $packageUrl"

Write-Host "2. Downloading and installing Azure Pipelines agent..." -ForegroundColor Cyan

$wc = New-Object System.Net.WebClient
$wc.DownloadFile($packageUrl, "$(Get-Location)/agent.$archSfx")

if ($IsLinux) {
  tar -xzf agent.$archSfx -C $AgentBinDir
} else {
  Expand-Archive -Path "agent.$archSfx" -DestinationPath $AgentBinDir
}

Set-Location $AgentBinDir

$agentName = $(if (Test-Path Env:AZP_AGENT_NAME) { ${Env:AZP_AGENT_NAME} } else { hostname })
$poolName = $(if (Test-Path Env:AZP_POOL) { ${Env:AZP_POOL} } else { 'Default' })
$agentWorkingDir = $(if (Test-Path Env:AZP_WORK) { ${Env:AZP_WORK} } else { '_work' })

try
{
  Write-Host "3. Configuring Azure Pipelines agent..." -ForegroundColor Cyan

  $sfx = if($IsLinux) { "sh" } else { "cmd" }

  if ($IsLinux) {
    chmod +x ./config.sh
    chmod +x ./run.sh
  } 

  . "./config.$sfx" --unattended `
    --agent $agentName `
    --url "$(${Env:AZP_URL})" `
    --auth PAT `
    --token "$pat" `
    --pool $poolName `
    --work $agentWorkingDir `
    --replace

  . "./config.$sfx" --unattended `
    --agent $agentName `
    --url "$(${Env:AZP_URL})" `
    --auth PAT `
    --token "$pat" `
    --pool $poolName `
    --work $agentWorkingDir `
    --replace

  Write-Host "4. Running Azure Pipelines agent..." -ForegroundColor Cyan

  $taskUrl = $Env:AZP_TASK_URL

  # Clear environment variables before running
  [Functions]::ClearEnvironmentVars($Env:VSO_AGENT_IGNORE);

  if ($taskUrl) {
    Write-Host "Running agent in task context. TaskUrl=$taskUrl"

    . azputils runtaskcmd `
      --taskUrl "$taskUrl" `
      --token "$pat" `
      -- "run.$sfx" --once
  } else {
    Write-Host "Running agent outside taskUrl"
    
    . "./run.$sfx" --once
  }

  # $exitCode = [Program]::Run($PWD, $sfx)
  # "./run.$sfx" --once
  $exitCode = $LASTEXITCODE

  Write-Host "4. Finished running job (Exit code:$exitCode)" -ForegroundColor Cyan
  exit 0;
}
finally
{
  if ($agentName -ieq "Placeholder") {
      Write-Host "Skipping cleanup. This is a placeholder agent."
  } else {
    Write-Host "Cleanup. Removing Azure Pipelines agent..." -ForegroundColor Cyan

    ./config.cmd remove --unattended `
      --auth PAT `
      --token "$pat"
  }
}