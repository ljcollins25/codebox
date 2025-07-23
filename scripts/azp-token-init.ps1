$azureDevopsResourceId = "499b84ac-1321-427f-aa17-267ca6975798"
$token = (az account get-access-token --resource $azureDevopsResourceId) | ConvertFrom-Json

${Env:Azputils.AccessToken} = $token.accessToken

$token.accessToken