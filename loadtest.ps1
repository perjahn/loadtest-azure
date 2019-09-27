Set-StrictMode -v latest
$ErrorActionPreference = "Stop"

[string] $logfolder = (pwd).Path

function Main($mainargs)
{
    [Diagnostics.Stopwatch] $watch = [Diagnostics.Stopwatch]::StartNew()

    if (!$mainargs -or $mainargs.Count -ne 3)
    {
        Log ("Usage: pwsh loadtest.ps1 <subscription> <resourcegroup> <storageaccount>") Red
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

    [string] $payloadFile = "payload.7z"
    if (Test-Path $payloadFile)
    {
        Log ("Deleting payload file: '" + $payloadFile + "'")
        del $payloadFile
    }

    Load-Dependencies

    [string] $artilleryYml = Get-ArtilleryYml



    Log ("Retrieving public ip address.")
    [string] $ipaddress = (Invoke-RestMethod -Uri "https://api.ipify.org?format=json").ip
    Log ("Got public ip address: " + $ipaddress)

    [string] $username = "loadadmin"

    Log ("Generating vm password.")
    [string] $password = Generate-AlphanumericPassword 24

    Prepare-Beat $payloadFolder "metricbeat" $env:MetricbeatYml
    Prepare-Beat $payloadFolder "filebeat" $env:FilebeatYml

    Log ("Generating payload password.")
    [string] $zipPassword = Generate-AlphanumericPassword 24

    Prepare-Payload $payloadFolder $payloadFile $zipPassword $artilleryYml


    Login $subscriptionName

    $resourceGroup = Get-AzResourceGroup -Name $resourceGroupName -ErrorAction SilentlyContinue
    if (!$resourceGroup)
    {
        Log ("Creating resource group: '" + $resourceGroupName + "' at '" + $location + "'")
        New-AzResourceGroup -Name $resourceGroupName -Location $location
    }
    else
    {
        Log ("Using existing resource group: '" + $resourceGroupName + "'")
    }

    [DateTime] $now = Get-Date

    $sasurls = Upload-Payload $payloadFile $resourceGroupName $location $storageAccountName $now
    Update-ParametersFile $parametersFile $username $password $ipaddress $zipPassword $sasurls

    Log ("Deploying: '" + $resourceGroupName + "' '" + $templateFile + "' '" + $parametersFile + "'")
    try
    {
        Log-TCTime "LoadTestCreateResourceGroup" { New-AzResourceGroupDeployment -ResourceGroupName $resourceGroupName -TemplateFile $templateFile -TemplateParameterFile $parametersFile }
    }
    catch
    {
        Log ("Couldn't deploy resources: " + $_.Exception.ToString()) Yellow
    }

    try
    {
        Download-Result $resourceGroupName $storageAccountName $sasurls["blobUrl"] $sasurls["containerName"] "result.7z" $zipPassword
    }
    catch
    {
        Log ("Couldn't download result: " + $_.Exception.ToString()) Yellow
    }

    if (!$env:DontDelete)
    {
        Log ("Deleting resource group: '" + $resourceGroupName + "'")
        try
        {
            Log-TCTime "LoadTestRemoveResourceGroup" { Remove-AzResourceGroup $resourceGroupName -Force }
        }
        catch
        {
            Log ("Couldn't delete resource group: " + $_.Exception.ToString()) Yellow
        }
    }

    Log ("Done: " + $watch.Elapsed)
}

function Load-Dependencies()
{
    [string] $nugetpkg = "https://www.nuget.org/api/v2/package/Newtonsoft.Json/12.0.2"
    [string] $tmpfolder = [IO.Path]::GetTempPath()
    [string] $zipfile = Join-Path $tmpfolder "json.zip"
    [string] $dllfolder = Join-Path $tmpfolder "jsondll"
    [string] $dllfile = Join-Path $tmpfolder "jsondll" "lib" "netstandard2.0" "Newtonsoft.Json.dll"

    if (Test-Path $dllfile)
    {
        Log ("File already downloaded: '" + $dllfile + "'")
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

        Log ("Extracting: '" + $zipfile + "' -> '" + $dllfolder + "'")
        Expand-Archive $zipfile $dllfolder

        if (!(Test-Path $dllfile))
        {
            Log ("Couldn't extract: '" + $dllfile + "'") Red
            exit 1
        }

        Log ("Deleting file: '" + $zipfile + "'")
        del $zipfile
    }

    Log ("Loading assembly: '" + $dllfile + "'")
    [Reflection.Assembly]::LoadFile($dllfile) | Out-Null
}

function Login([string] $subscriptionName)
{
    [string] $tenantId = $env:AzureTenantId
    [string] $clientId = $env:AzureClientId
    [string] $clientSecret = $env:AzureClientSecret

    Log ("Logging in: '" + $subscriptionName + "'")
    if ($tenantId -and $clientId -and $clientSecret)
    {
        $ss = $clientSecret | ConvertTo-SecureString -Force -AsPlainText
        $creds = New-Object PSCredential -ArgumentList $clientId, $ss

        Connect-AzAccount -Subscription $subscriptionName -ServicePrincipal -Tenant $tenantId -Credential $creds
    }
    else
    {
        Connect-AzAccount -Subscription $subscriptionName | Out-Null
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

function Prepare-Beat([string] $payloadFolder, [string] $beatName, [string] $beatYml, [string] $beatFolder)
{
    if (!$beatYml)
    {
        Log ("Ignoring " + $beatName + ".") Yellow
        return
    }

    if (!(Test-Path $payloadFolder))
    {
        Log ("Creating payload folder: '" + $payloadFolder + "'")
        md $payloadFolder | Out-Null
    }

    [string] $beatFile = Join-Path $payloadFolder ($beatName + ".yml")
    Log ("Saving " + $beatName + " file: '" + $beatFile + "'")
    Set-Content $beatFile $beatYml
}

function Prepare-Payload([string] $payloadFolder, [string] $payloadFile, [string] $zipPassword, [string] $artilleryYml)
{
    if (!(Test-Path $payloadFolder))
    {
        Log ("Creating payload folder: '" + $payloadFolder + "'")
        md $payloadFolder | Out-Null
    }

    [string] $zipfile = Join-Path ".." $payloadFile
    [string] $filename = Join-Path $payloadFolder "artillery.yml"

    Set-Content $filename $artilleryYml

    cd $payloadFolder
    Log ("Current dir: '" + (pwd).Path + "'")
    Log ("Zipping: . -> '" + $zipfile + "'")
    7z a -mx9 $zipfile -mhe ("-p" + $zipPassword)
    if (!$? -or (!(Test-Path $zipfile)) -or (dir $zipfile).Length -lt 1)
    {
        cd ..
        Log ("Couldn't zip.") Red
        exit 1
    }
    cd ..
}

function Upload-Payload([string] $payloadFile, [string] $resourceGroupName, [string] $location, [string] $storageAccountName, [DateTime] $now)
{
    [string] $sevenZip = "sevenzip.zip"

    [string] $storageKind = "BlobStorage"
    [string] $containerName = [Guid]::NewGuid().ToString()
    [string] $type = "Standard_LRS"
    [string] $accessTier = "Hot"


    Log ("Creating storage account: '" + $storageAccountName + "' in '" + $resourceGroupName + "' at '" + $location + "'")
    $storageAccount = $null
    try
    {
        $storageAccount = New-AzStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAccountName -Location $location -Type $type -Kind $storageKind -AccessTier $accessTier -EnableHttpsTrafficOnly $true
    }
    catch
    {
        Log $_.Exception Yellow

        Log ("Retrieving deployment storage account")
        $storageAccount = Get-AzStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAccountName
    }

    Log ("Retrieving deployment storage account key")
    $storageAccountKey = Get-AzStorageAccountKey -ResourceGroupName $resourceGroupName -Name $storageAccount.StorageAccountName
    $storageKey = $storageAccountKey[0].Value

    Log ("Creating deployment storage context")
    $storageContext = New-AzStorageContext -StorageAccountName $storageAccount.StorageAccountName -StorageAccountKey $storageKey

    Run-Robust {
            Log ("Creating deployment storage container (try " + ($i+1) + "): '" + $containerName + "'")
            New-AzStorageContainer -Name $containerName -Context $storageContext | Out-Null
        } { Clear-DnsClientCache } 12 5

    [DateTime] $endtime = $now.AddHours(1.0)

    if ((Test-Path "setup.ps1") -and (Test-Path $sevenZip))
    {
        [string] $setupScript = "setup.ps1"
        Log ("Windows, using setup script: '" + $setupScript + "'")
        [string[]] $keys = "setupScriptUrl", "sevenZipUrl", "payloadZipUrl"
        [string[]] $files = $setupScript, $sevenZip, $payloadFile
    }
    elseif ((Test-Path "setup.sh"))
    {
        [string] $setupScript = "setup.sh"
        Log ("Linux, using setup script: '" + $setupScript + "'")
        [string[]] $keys = "setupScriptUrl", "payloadZipUrl"
        [string[]] $files = $setupScript, $payloadFile
    }
    else
    {
        Log ("Unknown environment, setup.ps1/setup.sh missing.") Red
        exit 1
    }

    if (!(Test-Path $payloadFile))
    {
        Log ("Missing payload file: '" + $payloadFile + "'") Red
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

function Run-Robust([ScriptBlock] $main, [ScriptBlock] $cleanup, [int] $retries, [int] $sleepSeconds)
{
    for ([int] $i=0; $i -lt $retries; $i++)
    {
        try
        {
            &$main
            break
        }
        catch
        {
            Log $_.Exception
            if ($i -eq ($retries-1))
            {
                throw
            }
            if ($cleanup)
            {
                &$cleanup
            }
            Start-Sleep $sleepSeconds
        }
    }
}

function Upload-Blob($storageContext, [string] $containerName, [string] $filename, [DateTime] $now, [DateTime] $endtime)
{
    [string] $blobName = Split-Path -Leaf $filename
    [string] $url = $storageContext.BlobEndPoint + $containerName + "/" + $blobName

    Log ("Uploading '" + $filename + "' to '" + $url + "'")
    Set-AzStorageBlobContent -Container $containerName -File $filename -Blob $blobName -Context $storageContext -BlobType "Block" -Force | Out-Null

    [string] $sas = New-AzStorageBlobSASToken -Container $containerName -Blob $blobName -Context $storageContext -Permission r -StartTime $now -ExpiryTime $endtime
    [string] $sasurl = $url + $sas
    Log ("Got sasurl: '" + $sasurl + "'")

    return $sasurl
}

function Update-ParametersFile([string] $parametersFile, [string] $username, [string] $password, [string] $ipaddress, [string] $zipPassword, $sasurls)
{
    $replaceValues = @{}
    $replaceValues["adminUsername"] = $username
    $replaceValues["adminPassword"] = $password
    $replaceValues["sourceAddressPrefix"] = $ipaddress
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

    foreach ($elementName in ($replaceValues.Keys | sort))
    {
        $elements = @($json.parameters.Descendants() | ? { ($_.GetType().Name -eq "JProperty") -and ($_.Name -eq $elementName) })
        if (!$elements)
        {
            Log ("Couldn't find element: '" + $elementName + "'") Yellow
            continue
        }

        foreach ($element in $elements)
        {
            [string] $newvalue = $replaceValues[$elementName]

            if ($element.children().children())
            {
                if ($element.value.value.value -ne $newvalue)
                {
                    Log ("Updating .value '" + $filename + "', " + $element.Name + ": '" + $element.value.value.value + "' -> '" + (Obfuscate-String $newvalue $element.Name) + "'")
                    $element.value.value = $newvalue
                    $changed = $true
                }
            }
            else
            {
                if ($element.value -ne $newvalue)
                {
                    Log ("Updating '" + $filename + "', " + $element.Name + ": '" + $element.value + "' -> '" + (Obfuscate-String $newvalue $element.Name) + "'")
                    $element.value.value = $newvalue
                    $changed = $true
                }
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
    Log ("Retrieving result storage account")
    $storageAccount = Get-AzStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAccountName

    Log ("Retrieving deployment storage account key")
    $storageAccountKey = Get-AzStorageAccountKey -ResourceGroupName $resourceGroupName -Name $storageAccount.StorageAccountName
    $storageKey = $storageAccountKey[0].Value

    Log ("Creating deployment storage context")
    $storageContext = New-AzStorageContext -StorageAccountName $storageAccount.StorageAccountName -StorageAccountKey $storageKey


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
    Get-AzStorageBlobContent -Container $containerName -Blob $blobName -Context $storageContext | Out-Null

    7z x $zipfile ("-p" + $zipPassword)
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

function Generate-AlphanumericPassword([int] $numberOfChars)
{
    [char[]] $validChars = 'a'..'z' + 'A'..'Z' + [char]'0'..[char]'9'
    [string] $password = ""
    do
    {
        [string] $password = (1..$numberOfChars | % { $validChars[(Get-Random -Maximum $validChars.Length)] }) -join ""
    }
    while (
        !($password | ? { ($_.ToCharArray() | ? { [Char]::IsUpper($_) }) }) -or
        !($password | ? { ($_.ToCharArray() | ? { [Char]::IsLower($_) }) }) -or
        !($password | ? { ($_.ToCharArray() | ? { [Char]::IsDigit($_) }) }));

    return $password
}

function Log-TCTime([string] $key, [ScriptBlock] $script)
{
    [Diagnostics.Stopwatch] $watch = [Diagnostics.Stopwatch]::StartNew()

    try
    {
        &$script
    }
    finally
    {
        Log("##teamcity[buildStatisticValue key='" + $key + "' value='" + [int]$watch.Elapsed.TotalMilliseconds + "']") Magenta
    }
}

function Log([string] $message, $color)
{
    [string] $logfile = Join-Path $logfolder "create.log"

    [string] $annotatedMessage = [DateTime]::UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + ": " + $message
    $annotatedMessage | Add-Content $logfile

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
