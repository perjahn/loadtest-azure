Set-StrictMode -v latest
$ErrorActionPreference = "Stop"

[string] $logfolder = (pwd).Path

function Main($mainargs)
{
    [Diagnostics.Stopwatch] $watch = [Diagnostics.Stopwatch]::StartNew()

    if (!$mainargs -or $mainargs.Count -ne 3)
    {
        Log ("Usage: powershell .\create.ps1 <subscription> <resourcegroup> <storageaccount>") Red
        exit 1
    }

    [string] $subscriptionName = $mainargs[0]
    [string] $resourceGroupName = $mainargs[1]
    [string] $storageAccountName = $mainargs[2]

    [string] $location = "West Europe"
    [string] $templateFile = "template.json"
    [string] $parametersFile = "parameters.json"

    if (!(Test-Path $templateFile))
    {
        Log ("Template file missing: '" + $templateFile + "'") Red
        exit 1
    }
    if (!(Test-Path $parametersFile))
    {
        Log ("Parameters file missing: '" + $parametersFile + "'") Red
        exit 1
    }

    [string] $payloadFolder = "payload"
    if (Test-Path $payloadFolder)
    {
        Log ("Deleting payload folder: '" + $payloadFolder + "'")
        rd -Recurse -Force $payloadFolder
    }

    Load-Dependencies

    [string] $artilleryYml = Get-ArtilleryYml



    Log ("Retrieving public ip address.")
    [string] $ipaddress = (Invoke-RestMethod -Uri "https://api.ipify.org?format=json").ip
    Log ("Got public ip address: " + $ipaddress)

    [string] $username = "loadadmin"

    Log ("Generating password.")
    [string] $password = Generate-AlphanumericPassword 24

    Prepare-Beat "metricbeat" $env:MetricbeatYml
    Prepare-Beat "filebeat" $env:FilebeatYml

    Log ("Generating payload password.")
    [string] $zipPassword = Generate-AlphanumericPassword 24

    Prepare-Payload $zipPassword $artilleryYml


    Login

    Log ("Selecting subscription: '" + $SubscriptionName + "'")
    Select-AzureRmSubscription -SubscriptionName $SubscriptionName | Out-Null

    $resourceGroup = Get-AzureRmResourceGroup -Name $resourceGroupName -ErrorAction SilentlyContinue
    if (!$resourceGroup)
    {
        Log ("Creating resource group: '" + $resourceGroupName + "' at '" + $location + "'")
        New-AzureRmResourceGroup -Name $resourceGroupName -Location $location
    }
    else
    {
        Log ("Using existing resource group: '" + $resourceGroupName + "'")
    }

    $sasurls = Upload-Payload $resourceGroupName $location $storageAccountName
    Update-ParametersFile $parametersFile $resourceGroupName $location $username $password $ipaddress $zipPassword $sasurls

    Log ("Deploying: '" + $resourceGroupName + "' '" + $templateFile + "' '" + $parametersFile + "'")
    New-AzureRmResourceGroupDeployment -ResourceGroupName $resourceGroupName -TemplateFile $templateFile -TemplateParameterFile $parametersFile

    Download-Result $resourceGroupName $storageAccountName $sasurls["blobUrl"] $sasurls["containerName"] "result.7z" $zipPassword

    Log ("Deleting resource group: '" + $resourceGroupName + "'")
    Remove-AzureRmResourceGroup $resourceGroupName -Force

    Log ("Done: " + $watch.Elapsed)
}

function Load-Dependencies()
{
    if ([Environment]::Version.Major -lt 4)
    {
        Log ("Newtonsoft.Json 11.0.2 requires .net 4 (Powershell 3.0), you have: " + [Environment]::Version) Red
        exit 1
    }

    [string] $nugetpkg = "https://www.nuget.org/api/v2/package/Newtonsoft.Json/11.0.2"
    [string] $zipfile = Join-Path $env:temp "json.zip"
    [string] $dllfile = "Newtonsoft.Json.dll"
    [string] $zipfilepath = Join-Path (Join-Path $zipfile "lib\net45") $dllfile
    [string] $dllfilepath = Join-Path $env:temp $dllfile

    if (Test-Path $dllfilepath)
    {
        Log ("File already downloaded: '" + $dllfilepath + "'")
    }
    else
    {
        Log ("Downloading: '" + $nugetpkg + "' -> '" + $zipfile + "'")
        Invoke-WebRequest -UseBasicParsing $nugetpkg -OutFile $zipfile
        if (!(Test-Path $zipfile))
        {
            Log ("Couldn't download: '" + $zipfile + "'") Red
            exit 1
        }

        Log ("Extracting: '" + $zipfilepath + "' -> '" + $env:temp + "'")
        $shell = New-Object -com Shell.Application
        $shell.Namespace($env:temp).CopyHere($zipfilepath, 20)

        if (!(Test-Path $dllfilepath))
        {
            Log ("Couldn't extract: '" + $dllfilepath + "'") Red
            exit 1
        }

        Log ("Deleting file: '" + $zipfile + "'")
        del $zipfile
    }

    Log ("Loading assembly: '" + $dllfilepath + "'")
    [Reflection.Assembly]::LoadFile($dllfilepath) | Out-Null
}

function Login()
{
    [string] $tenantId = $env:AzureTenantId
    [string] $subscriptionId = $env:AzureSubscriptionId
    [string] $clientId = $env:AzureClientId
    [string] $clientSecret = $env:AzureClientSecret

    Log ("Logging in...")
    if ($tenantId -and $subscriptionId -and $clientId -and $clientSecret)
    {
        $ss = $clientSecret | ConvertTo-SecureString -Force -AsPlainText
        $creds = New-Object PSCredential -ArgumentList $clientId, $ss

        Connect-AzureRmAccount -ServicePrincipal -TenantId $tenantId -SubscriptionId $subscriptionId -Credential $creds
    }
    else
    {
        Login-AzureRmAccount | Out-Null
    }
}

function Get-ArtilleryYml()
{   
    $artilleryYml = $null

    if (!$env:ArtilleryYml)
    {
        Log ("Environment variable ArtilleryYml not set.") Red
        exit 1
    }
    else
    {
        [string] $artilleryYml = $env:ArtilleryYml
        if (!$artilleryYml.Contains("config:") -or !$artilleryYml.Contains("scenarios:"))
        {
            Log ("Environment variable ArtilleryYml not a valid Artillery configuration!") Red
            exit 1
        }
    }

    return $artilleryYml
}

function Prepare-Beat([string] $beatName, [string] $beatYml, [string] $beatFolder)
{
    [string] $payloadFolder = "payload"

    if ($beatYml)
    {
        if (!(Test-Path $payloadFolder))
        {
            Log ("Creating payload folder: '" + $payloadFolder + "'")
            md $payloadFolder | Out-Null
        }

        [string] $targetFolder = Join-Path $payloadFolder $beatName
        if ($beatFolder)
        {
            if (!(Test-Path $beatFolder))
            {
                Log ("Missing " + $beatName + " folder: '" + $beatFolder + "'") Red
                exit 1
            }
            Log ("Copying " + $beatName + " folder: '" + $beatFolder + "' -> '" + $payloadFolder + "'")
            copy $beatFolder $payloadFolder -Recurse
        }
        else
        {
            Log ("Creating " + $beatName + " folder: '" + $targetFolder + "'")
            md $targetFolder | Out-Null
        }

        [string] $beatFile = Join-Path $targetFolder ($beatName + ".yml")
        Log ("Saving " + $beatName + " file: '" + $beatFile + "'")
        sc $beatFile $beatYml
    }
    else
    {
        Log ("Ignoring " + $beatName + ".") Yellow
    }
}

function Prepare-Payload([string] $zipPassword, [string] $artilleryYml)
{
    Set-Alias zip "C:\Program Files\7-Zip\7z.exe"

    [string] $zipfile = "payload.7z"
    [string] $filename = "artillery.yml"

    if (Test-Path $zipfile)
    {
        Log ("Deleting zipfile: '" + $zipfile + "'")
        del $zipfile
    }

    $artilleryYml | sc $filename

    Log ("Zipping: '" + $filename + "' -> '" + $zipfile + "'")
    zip a -mx9 $zipfile $filename -mhe ("-p" + $zipPassword)
    if (!$? -or (!(Test-Path $zipfile)) -or (dir $zipfile).Length -lt 1)
    {
        Log ("Couldn't zip.") Red
        exit 1
    }
}

function Upload-Payload([string] $resourceGroupName, [string] $location, [string] $storageAccountName)
{
    [string] $sevenZip = "sevenzip.zip"
    [string] $payloadZip = "payload.7z"

    [string] $storageKind = "BlobStorage"
    [string] $containerName = [Guid]::NewGuid().ToString()
    [string] $type = "Standard_LRS"
    [string] $accessTier = "Hot"


    Log ("Creating storage account: '" + $storageAccountName + "' in '" + $resourceGroupName + "' at '" + $location + "'")
    $storageAccount = $null
    try
    {
        $storageAccount = New-AzureRmStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAccountName -Location $location -Type $type -Kind $storageKind -AccessTier $accessTier
        # -EnableEncryptionService -EnableHttpsTrafficOnly
    }
    catch
    {
        Log $_.Exception Yellow

        Log ("Retrieving deployment storage account")
        $storageAccount = Get-AzureRmStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAccountName
    }

    Log ("Retrieving deployment storage account key")
    $storageAccountKey = Get-AzureRmStorageAccountKey -ResourceGroupName $resourceGroupName -Name $storageAccount.StorageAccountName
    $storageKey = $storageAccountKey[0].Value

    Log ("Creating deployment storage context")
    $storageContext = New-AzureStorageContext -StorageAccountName $storageAccount.StorageAccountName -StorageAccountKey $storageKey

    Log ("Creating deployment storage container: '" + $containerName + "'")
    New-AzureStorageContainer -Name $containerName -Context $storageContext | Out-Null

    [DateTime] $now = Get-Date
    [DateTime] $endtime = $now.AddHours(1.0)

    if ((Test-Path "setup.ps1") -and (Test-Path $sevenZip))
    {
        [string] $setupScript = "setup.ps1"
        Log ("Windows, using setup script: '" + $setupScript + "'")
        [string[]] $keys = "setupScriptUrl", "sevenZipUrl", "payloadZipUrl"
        [string[]] $files = $setupScript, $sevenZip, $payloadZip
    }
    elseif ((Test-Path "setup.sh"))
    {
        [string] $setupScript = "setup.sh"
        Log ("Linux, using setup script: '" + $setupScript + "'")
        [string[]] $keys = "setupScriptUrl", "payloadZipUrl"
        [string[]] $files = $setupScript, $payloadZip
    }
    else
    {
        Log ("Unknown environment, setup.ps1/setup.sh missing.") Red
        exit 1
    }

    if (!(Test-Path $payloadZip))
    {
        Log ("Missing payload file: '" + $payloadZip + "'")
        exit 1
    }


    $urls = @{}
    Log ("Uploading files...")
    for ([int] $i=0; $i -lt $keys.Count; $i++)
    {
        [string] $key = $keys[$i]
        [string] $filename = $files[$i]

        $urls[$key] = Upload-Blob $storageContext $containerName $filename $now $endtime
    }


    $urls["blobUrl"] = $storageContext.BlobEndPoint + $containerName
    $urls["storageKey"] = $storageKey
    $urls["containerName"] = $containerName


    return $urls
}

function Upload-Blob($storageContext, [string] $containerName, [string] $filename, [DateTime] $now, [DateTime] $endtime)
{
    [string] $blobName = Split-Path -Leaf $filename
    [string] $url = $storageContext.BlobEndPoint + $containerName + "/" + $blobName

    Log ("Uploading '" + $filename + "' to '" + $url + "'")
    Set-AzureStorageBlobContent -Container $containerName -File $filename -Blob $blobName -Context $storageContext -BlobType "Block" -Force | Out-Null

    [string] $sas = New-AzureStorageBlobSASToken -Container $containerName -Blob $blobName -Context $storageContext -Permission r -StartTime $now -ExpiryTime $endtime
    [string] $sasurl = $url + $sas
    Log ("Got sasurl: '" + $sasurl + "'")

    return $sasurl
}

function Update-ParametersFile([string] $parametersFile, [string] $resourceGroupName, [string] $location, [string] $username, [string] $password, [string] $ipaddress, [string] $zipPassword, $sasurls)
{
    $replaceValues = @{}
    $replaceValues["vnetResourceGroupName"] = $resourceGroupName
    $replaceValues["location"] = $location
    $replaceValues["adminUsername"] = $username
    $replaceValues["adminPassword"] = $password
    $replaceValues["firewallIpAddress"] = $ipaddress
    $replaceValues["zipPassword"] = $zipPassword

    $sasurls.Keys | % {
        $replaceValues[$_] = $sasurls[$_]
    }


    [string] $filename = Join-Path (pwd).Path $parametersFile
    Log ("Reading parameters file: '" + $filename + "'")
    [string] $content = [IO.File]::ReadAllText($filename)
    $json = [Newtonsoft.Json.Linq.JToken]::Parse($content)


    if (!$json.parameters)
    {
        Log ("Couldn't find any parameters in file: '" + $filename + "'") Red
        exit 1
    }

    [bool] $changed = $false

    $json.parameters.Children() | % {
        $element = $_
        if ($replaceValues | ? { $_.ContainsKey($element.Name.ToLower()) })
        {
            [string] $newvalue = $replaceValues[$element.Name.ToLower()]

            if ($element.value.value.value -ne $newvalue)
            {
                Log ("Updating '" + $filename + "', " + $element.Name + ": '" + $element.value.value.value + "' -> '" + (Obfuscate-String $newvalue $element.Name) + "'")
                $element.value.value = $newvalue
                $changed = $true
            }
        }
    }

    if ($changed)
    {
        [string] $pretty = $json.ToString([Newtonsoft.Json.Formatting]::Indented)

        Log ("Saving parameters file: '" + $filename + "'")
        [IO.File]::WriteAllText($filename, $pretty)
    }
}

function Download-Result([string] $resourceGroupName, [string] $storageAccountName, [string] $blobUrl, [string] $containerName, [string] $zipfile, [string] $zipPassword)
{
    Set-Alias zip "C:\Program Files\7-Zip\7z.exe"

    Log ("Retrieving result storage account")
    $storageAccount = Get-AzureRmStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAccountName

    Log ("Retrieving deployment storage account key")
    $storageAccountKey = Get-AzureRmStorageAccountKey -ResourceGroupName $resourceGroupName -Name $storageAccount.StorageAccountName
    $storageKey = $storageAccountKey[0].Value

    Log ("Creating deployment storage context")
    $storageContext = New-AzureStorageContext -StorageAccountName $storageAccount.StorageAccountName -StorageAccountKey $storageKey


    [string] $url = $blobUrl + "/" + $zipfile
    [string] $zipfile = "result.7z"
    [string] $blobName = "result.7z"

    if (Test-Path $zipfile)
    {
        Log ("Deleting zipfile: '" + $zipfile + "'")
        del $zipfile
    }
    [string] $jsonfile = "result.json"
    if (Test-Path $jsonfile)
    {
        Log ("Deleting jsonfile: '" + $jsonfile + "'")
        del $jsonfile
    }
    [string] $htmlfile = "result.html"
    if (Test-Path $htmlfile)
    {
        Log ("Deleting htmlfile: '" + $htmlfile + "'")
        del $htmlfile
    }
    [string] $stdfile = "stdout"
    if (Test-Path $stdfile)
    {
        Log ("Deleting stdfile: '" + $stdfile + "'")
        del $stdfile
    }
    [string] $errfile = "errout"
    if (Test-Path $errfile)
    {
        Log ("Deleting errfile: '" + $errfile + "'")
        del $errfile
    }

    Log ("Downloading '" + $url + "' to '" + $zipfile + "'")
    Get-AzureStorageBlobContent -Container $containerName -Blob $blobName -Context $storageContext | Out-Null

    zip x $zipfile ("-p" + $zipPassword)
}

function Obfuscate-String([string] $text, [string] $textname)
{
    if ($textname.ToLower().Contains("password") -or $textname.ToLower().EndsWith("key"))
    {
        return "*" * $text.Length
    }
    else
    {
        return $text
    }
}

function Generate-AlphanumericPassword([int] $chars)
{
    Import-Module "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Web.dll"

    [string] $password = ""
    do
    {
        [string] $password = [System.Web.Security.Membership]::GeneratePassword($chars,0)
    }
    while (($password | ? { ($_.ToCharArray() | ? { ![Char]::IsLetterOrDigit($_) }) }) -or
        !($password | ? { ($_.ToCharArray() | ? { [Char]::IsUpper($_) }) }) -or
        !($password | ? { ($_.ToCharArray() | ? { [Char]::IsLower($_) }) }) -or
        !($password | ? { ($_.ToCharArray() | ? { [Char]::IsDigit($_) }) }));

    return $password
}

function Log([string] $message, $color)
{
    [string] $logfile = Join-Path $logfolder "create.log"

    [string] $annotatedMessage = [DateTime]::UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + ": " + $message
    $annotatedMessage | ac $logfile

    if ($color)
    {
        Write-Host $annotatedMessage -f $color
    }
    else
    {
        Write-Host $annotatedMessage -f Green
    }
}

Main $args
