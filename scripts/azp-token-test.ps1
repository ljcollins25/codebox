# Add-PathToSystem.ps1

param (
    [string]$Token,
    [string]$Organization
)

$authValue = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(":" + $token))

$headers = @{
    Authorization = "Basic $authValue";
    'X-VSS-ForceMsaPassThrough' = $true
}

$pipelineRunUrl = "https://dev.azure.com/$organization/_apis/projects"

Write-Output "Pipeline Run URL: $pipelineRunUrl"

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$response = Invoke-WebRequest -Uri $pipelineRunUrl -Method GET -Headers $headers -ContentType 'application/json'
$statusCode = $response.StatusCode
Write-Output "Status Code: $statusCode"

$statusName = @{
        200 = "OK"
        201 = "Created"
        202 = "Accepted"
        203 = "NonAuthoritativeInformation"
        204 = "NoContent"
        400 = "BadRequest"
        401 = "Unauthorized"
        403 = "Forbidden"
        404 = "NotFound"
        500 = "InternalServerError"
        502 = "BadGateway"
        503 = "ServiceUnavailable"
    }[$statusCode]

    if ($statusName) {
        Write-Output "$statusCode $statusName"
    } else {
        Write-Output "$statusCode UnknownStatus"
    }