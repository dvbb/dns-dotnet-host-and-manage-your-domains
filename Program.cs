// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.Samples.Common;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager;

namespace ManageDns
{
    public class Program
    {
        private static ResourceIdentifier? _resourceGroupId = null;
        private const string CustomDomainName = "THE CUSTOM DOMAIN THAT YOU OWN (e.g. contoso.com)";

        /**
         * Azure DNS sample for managing DNS zones.
         *  - Create a root DNS zone (contoso.com)
         *  - Create a web application
         *  - Add a CNAME record (www) to root DNS zone and bind it to web application host name
         *  - Creates a virtual machine with public IP
         *  - Add a A record (employees) to root DNS zone that points to virtual machine public IPV4 address
         *  - Creates a child DNS zone (partners.contoso.com)
         *  - Creates a virtual machine with public IP
         *  - Add a A record (partners) to child DNS zone that points to virtual machine public IPV4 address
         *  - Delegate from root domain to child domain by adding NS records
         *  - Remove A record from the root DNS zone
         *  - Delete the child DNS zone
         */
        public static async Task RunSample(ArmClient client)
        {
            {
                // Get default subscription
                SubscriptionResource subscription = await client.GetDefaultSubscriptionAsync();

                // Create a resource group in the EastUS region
                string rgName = Utilities.CreateRandomName("PrivateDnsTemplateRG");
                Utilities.Log($"creating resource group...");
                ArmOperation<ResourceGroupResource> rgLro = await subscription.GetResourceGroups().CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(AzureLocation.EastUS));
                ResourceGroupResource resourceGroup = rgLro.Value;
                _resourceGroupId = resourceGroup.Id;
                Utilities.Log("Created a resource group with name: " + resourceGroup.Data.Name);

                //============================================================
                // Creates root DNS Zone

                Utilities.Log("Creating root DNS zone " + CustomDomainName + "...");
                var rootDnsZone = azure.DnsZones.Define(CustomDomainName)
                    .WithExistingResourceGroup(resourceGroup)
                    .Create();
                Utilities.Log("Created root DNS zone " + rootDnsZone.Name);
                Utilities.Print(rootDnsZone);

                //============================================================
                // Sets NS records in the parent zone (hosting custom domain) to make Azure DNS the authoritative
                // source for name resolution for the zone

                Utilities.Log("Go to your registrar portal and configure your domain " + CustomDomainName
                        + " with following name server addresses");
                foreach (var nameServer in rootDnsZone.NameServers)
                {
                    Utilities.Log(" " + nameServer);
                }
                Utilities.Log("Press [ENTER] after finishing above step");
                Utilities.ReadLine();

                //============================================================
                // Creates a web App

                Utilities.Log("Creating Web App " + webAppName + "...");
                var webApp = azure.WebApps.Define(webAppName)
                        .WithRegion(Region.USEast2)
                        .WithExistingResourceGroup(rgName)
                        .WithNewWindowsPlan(PricingTier.BasicB1)
                        .DefineSourceControl()
                            .WithPublicGitRepository("https://github.com/jianghaolu/azure-site-test")
                            .WithBranch("master")
                            .Attach()
                        .Create();
                Utilities.Log("Created web app " + webAppName);
                Utilities.Print(webApp);

                //============================================================
                // Creates a CName record and bind it with the web app

                // Step 1: Adds CName DNS record to root DNS zone that specify web app host domain as an
                // alias for www.[customDomainName]

                Utilities.Log("Updating DNS zone by adding a CName record...");
                rootDnsZone = rootDnsZone.Update()
                        .WithCNameRecordSet("www", webApp.DefaultHostName)
                        .Apply();
                Utilities.Log("DNS zone updated");
                Utilities.Print(rootDnsZone);

                // Waiting for a minute for DNS CName entry to propagate
                Utilities.Log("Waiting a minute for CName record entry to propagate...");
                SdkContext.DelayProvider.Delay(60 * 1000);

                // Step 2: Adds a web app host name binding for www.[customDomainName]
                //         This binding action will fail if the CName record propagation is not yet completed

                Utilities.Log("Updating Web app with host name binding...");
                webApp.Update()
                        .DefineHostnameBinding()
                            .WithThirdPartyDomain(CustomDomainName)
                            .WithSubDomain("www")
                            .WithDnsRecordType(CustomHostNameDnsRecordType.CName)
                            .Attach()
                        .Apply();
                Utilities.Log("Web app updated");
                Utilities.Print(webApp);



                //============================================================
                // Creates a virtual machine with public IP

                Utilities.Log("Creating a virtual machine with public IP...");
                var virtualMachine1 = azure.VirtualMachines
                        .Define(SdkContext.RandomResourceName("employeesvm-", 20))
                        .WithRegion(Region.USEast)
                        .WithExistingResourceGroup(resourceGroup)
                        .WithNewPrimaryNetwork("10.0.0.0/28")
                        .WithPrimaryPrivateIPAddressDynamic()
                        .WithNewPrimaryPublicIPAddress(SdkContext.RandomResourceName("empip-", 20))
                        .WithPopularWindowsImage(KnownWindowsVirtualMachineImage.WindowsServer2012R2Datacenter)
                        .WithAdminUsername("testuser")
                        .WithAdminPassword(Utilities.CreatePassword())
                        .WithSize(VirtualMachineSizeTypes.Parse("Standard_D2a_v4"))
                        .Create();
                Utilities.Log("Virtual machine created");

                //============================================================
                // Update DNS zone by adding a A record in root DNS zone pointing to virtual machine IPv4 address

                var vm1PublicIpAddress = virtualMachine1.GetPrimaryPublicIPAddress();
                Utilities.Log("Updating root DNS zone " + CustomDomainName + "...");
                rootDnsZone = rootDnsZone.Update()
                        .DefineARecordSet("employees")
                            .WithIPv4Address(vm1PublicIpAddress.IPAddress)
                            .Attach()
                        .Apply();
                Utilities.Log("Updated root DNS zone " + rootDnsZone.Name);
                Utilities.Print(rootDnsZone);

                // Prints the CName and A Records in the root DNS zone
                //
                Utilities.Log("Getting CName record set in the root DNS zone " + CustomDomainName + "...");
                var cnameRecordSets = rootDnsZone
                        .CNameRecordSets
                        .List();

                foreach (var cnameRecordSet in cnameRecordSets)
                {
                    Utilities.Log("Name: " + cnameRecordSet.Name + " Canonical Name: " + cnameRecordSet.CanonicalName);
                }

                Utilities.Log("Getting ARecord record set in the root DNS zone " + CustomDomainName + "...");
                var aRecordSets = rootDnsZone
                        .ARecordSets
                        .List();

                foreach (var aRecordSet in aRecordSets)
                {
                    Utilities.Log("Name: " + aRecordSet.Name);
                    foreach (var ipv4Address in aRecordSet.IPv4Addresses)
                    {
                        Utilities.Log("  " + ipv4Address);
                    }
                }

                //============================================================
                // Creates a child DNS zone

                var partnerSubDomainName = "partners." + CustomDomainName;
                Utilities.Log("Creating child DNS zone " + partnerSubDomainName + "...");
                var partnersDnsZone = azure.DnsZones
                        .Define(partnerSubDomainName)
                        .WithExistingResourceGroup(resourceGroup)
                        .Create();
                Utilities.Log("Created child DNS zone " + partnersDnsZone.Name);
                Utilities.Print(partnersDnsZone);

                //============================================================
                // Adds NS records in the root dns zone to delegate partners.[customDomainName] to child dns zone

                Utilities.Log("Updating root DNS zone " + rootDnsZone + "...");
                var nsRecordStage = rootDnsZone
                        .Update()
                        .DefineNSRecordSet("partners")
                        .WithNameServer(partnersDnsZone.NameServers[0]);
                for (int i = 1; i < partnersDnsZone.NameServers.Count(); i++)
                {
                    nsRecordStage = nsRecordStage.WithNameServer(partnersDnsZone.NameServers[i]);
                }
                nsRecordStage
                        .Attach()
                        .Apply();
                Utilities.Log("Root DNS zone updated");
                Utilities.Print(rootDnsZone);

                //============================================================
                // Creates a virtual machine with public IP

                Utilities.Log("Creating a virtual machine with public IP...");
                var virtualMachine2 = azure.VirtualMachines
                        .Define(SdkContext.RandomResourceName("partnersvm-", 20))
                        .WithRegion(Region.USEast)
                        .WithExistingResourceGroup(resourceGroup)
                        .WithNewPrimaryNetwork("10.0.0.0/28")
                        .WithPrimaryPrivateIPAddressDynamic()
                        .WithNewPrimaryPublicIPAddress(SdkContext.RandomResourceName("ptnerpip-", 20))
                        .WithPopularWindowsImage(KnownWindowsVirtualMachineImage.WindowsServer2012R2Datacenter)
                        .WithAdminUsername("testuser")
                        .WithAdminPassword(Utilities.CreatePassword())
                        .WithSize(VirtualMachineSizeTypes.Parse("Standard_D2a_v4"))
                        .Create();
                Utilities.Log("Virtual machine created");

                //============================================================
                // Update child DNS zone by adding a A record pointing to virtual machine IPv4 address

                var vm2PublicIpAddress = virtualMachine2.GetPrimaryPublicIPAddress();
                Utilities.Log("Updating child DNS zone " + partnerSubDomainName + "...");
                partnersDnsZone = partnersDnsZone.Update()
                        .DefineARecordSet("@")
                            .WithIPv4Address(vm2PublicIpAddress.IPAddress)
                            .Attach()
                        .Apply();
                Utilities.Log("Updated child DNS zone " + partnersDnsZone.Name);
                Utilities.Print(partnersDnsZone);

                //============================================================
                // Removes A record entry from the root DNS zone

                Utilities.Log("Removing A Record from root DNS zone " + rootDnsZone.Name + "...");
                rootDnsZone = rootDnsZone.Update()
                        .WithoutARecordSet("employees")
                        .Apply();
                Utilities.Log("Removed A Record from root DNS zone");
                Utilities.Print(rootDnsZone);

                //============================================================
                // Deletes the DNS zone

                Utilities.Log("Deleting child DNS zone " + partnersDnsZone.Name + "...");
                azure.DnsZones.DeleteById(partnersDnsZone.Id);
                Utilities.Log("Deleted child DNS zone " + partnersDnsZone.Name);
            }
            //finally
            //{
            //    try
            //    {
            //        if (_resourceGroupId is not null)
            //        {
            //            Utilities.Log($"Deleting Resource Group: {_resourceGroupId}");
            //            await client.GetResourceGroupResource(_resourceGroupId).DeleteAsync(WaitUntil.Completed);
            //            Utilities.Log($"Deleted Resource Group: {_resourceGroupId}");
            //        }
            //    }
            //    catch (Exception)
            //    {
            //        Utilities.Log("Did not create any resources in Azure. No clean up is necessary");
            //    }
            //}
        }

        public static async Task Main(string[] args)
        {
            {
                //=================================================================
                // Authenticate
                var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
                var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
                var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
                var subscription = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
                ClientSecretCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                ArmClient client = new ArmClient(credential, subscription);

                await RunSample(client);
            }
            //catch (Exception e)
            //{
            //    Utilities.Log(e);
            //}
        }
    }
}
