      $azureRegion = Invoke-RestMethod -Headers @{"Metadata"="true"} -Uri "http://169.254.169.254/metadata/instance/compute/location?api-version=2017-08-01&format=text"
      Write-Host $azureRegion

      Write-Host "##vso[task.setvariable variable=AzureRegion;]$azureRegion"