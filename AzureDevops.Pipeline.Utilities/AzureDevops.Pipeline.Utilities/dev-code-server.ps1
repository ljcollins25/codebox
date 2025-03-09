$ErrorActionPreference = 'Stop'

if ($IsLinux) {
  # Get the current user's UID
  $currentUserId = (id -u)
  if (($Env:IsSudo -ne "1") -and ($currentUserId -ne 0)) {
    Write-Host "Invoking IsSudo=$($Env:IsSudo)"
    $Env:IsSudo="1"
    sudo -E pwsh -File $($MyInvocation.MyCommand.Path)
    return;
  }
}

mkdir $Env:AZPUTILS_DCS_WORKSPACE/download

if ($IsLinux) {
  Invoke-WebRequest -Uri "https://code.visualstudio.com/sha/download?build=stable&os=cli-alpine-x64" -OutFile $Env:AZPUTILS_DCS_WORKSPACE/download/code.tar.gz

  . tar -xzf $Env:AZPUTILS_DCS_WORKSPACE/download/code.tar.gz -C $Env:AZPUTILS_DCS_WORKSPACE/download/

  . $Env:AZPUTILS_DCS_WORKSPACE/download/code tunnel user login --provider $Env:AZPUTILS_DCS_PROVIDER

  . $Env:AZPUTILS_DCS_WORKSPACE/download/code tunnel --name $Env:AZPUTILS_DCS_NAME --accept-server-license-terms
}
else {
  Invoke-WebRequest -Uri "https://code.visualstudio.com/sha/download?build=stable&os=cli-win32-x64" -OutFile $Env:AZPUTILS_DCS_WORKSPACE/download/code.zip

  Expand-Archive -Path $Env:AZPUTILS_DCS_WORKSPACE/download/code.zip -DestinationPath $Env:AZPUTILS_DCS_WORKSPACE/download/

  . $Env:AZPUTILS_DCS_WORKSPACE/download/code.exe tunnel user login --provider $Env:AZPUTILS_DCS_PROVIDER

  . $Env:AZPUTILS_DCS_WORKSPACE/download/code.exe tunnel --name $Env:AZPUTILS_DCS_NAME --accept-server-license-terms
}