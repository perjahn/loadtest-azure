What is this?
This repo contains a script (loadtest.ps1) that creates a temporary resource group containing a linux VM,
installs Artillery in it that runs a load test, and generates a report.

How do I configure Artillery?
You set the environment variable ArtilleryYml. This is the content of the configuration file for Artillery.
https://artillery.io/docs/getting-started

How do I configure Azure credentials?
First you need to create a service principal in your azure subscription.
Then you specify the service principal by setting 4 environment variables:
AzureTenantId, AzureSubscriptionId, AzureClientId, and AzureClientSecret.

What are the command line arguments to the script?
subscription, resourcegroup, storageaccount,
i.e. the name of the subscription, the name of the temporary resource group where the VM should
be created, and the name of the storage account (this must be globally unique).

How do I access the VM?
There's no need to login to the temporary VM, but it is created using a firewall (nsg) which only
accepts inbound traffic from the public ip where the script is executed. The admin username and
password that are generated are saved to the parameters.json file.

What are the output from the load test?
An html report file, named result.html. When the load test is finished, this file is retrieved
automatically from the VM.

For how long can the load test run?
Azure aborts scripts after 15 minutes.

How much iops do the VM have, and how much does it cost?
80k iops, which is the most you can get in Azure. The cheapest VM with 80k iops is used, it costs about 15 sek/h.
