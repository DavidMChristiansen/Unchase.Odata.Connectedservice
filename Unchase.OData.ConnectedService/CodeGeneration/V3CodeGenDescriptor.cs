﻿// Copyright (c) 2018 Unchase (https://github.com/unchase).  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using System.Data.Services.Design;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using EnvDTE;
using Microsoft.VisualStudio.ConnectedServices;
using Unchase.OData.ConnectedService.Common;
using Unchase.OData.ConnectedService.Models;

namespace Unchase.OData.ConnectedService.CodeGeneration
{
    internal class V3CodeGenDescriptor : BaseCodeGenDescriptor
    {
        public V3CodeGenDescriptor(string metadataUri, ConnectedServiceHandlerContext context, Project project)
            : base(metadataUri, context, project)
        {
            this.ClientNuGetPackageName = Common.Constants.V3ClientNuGetPackage;
            this.ValueTupleNuGetPackageName = Common.Constants.ValueTupleNuGetPackage;
            this.ClientDocUri = Common.Constants.V3DocUri;
            this.ServiceConfiguration = base.ServiceConfiguration as ServiceConfigurationV3;
        }

        private new ServiceConfigurationV3 ServiceConfiguration { get; set; }

        public override async Task AddNugetPackages()
        {
            await this.Context.Logger.WriteMessageAsync(LoggerMessageCategory.Information, "Adding Nuget Packages for OData V3...");

            var wcfDSInstallLocation = CodeGeneratorUtils.GetWCFDSInstallLocation();
            var packageSource = Path.Combine(wcfDSInstallLocation, @"bin\NuGet");
            if (string.IsNullOrEmpty(wcfDSInstallLocation))
                packageSource = Common.Constants.NuGetOnlineRepository;
            else
            {
                if (Directory.Exists(packageSource))
                {
                    var files = Directory.EnumerateFiles(packageSource, "*.nupkg").ToList();

                    if (files.Count == 0)
                        packageSource = Common.Constants.NuGetOnlineRepository;
                    else
                    {
                        foreach (var nugetPackage in Common.Constants.V3NuGetPackages)
                        {
                            if (!files.Any(f => Regex.IsMatch(f, nugetPackage + @"(.\d){2,4}.nupkg")))
                                packageSource = Common.Constants.NuGetOnlineRepository;
                        }
                    }
                }
                else
                    packageSource = Common.Constants.NuGetOnlineRepository;
            }

            if (ServiceConfiguration.IncludeExtensionsT4File)
                await CheckAndInstallNuGetPackage(packageSource, this.ValueTupleNuGetPackageName);

            if (packageSource == Common.Constants.NuGetOnlineRepository)
            {
                foreach (var nugetPackage in Common.Constants.V3NuGetPackages)
                {
                    await CheckAndInstallNuGetPackage(packageSource, nugetPackage);
                }
            }
            else
                await CheckAndInstallNuGetPackage(packageSource, this.ClientNuGetPackageName);

        }

        internal async Task CheckAndInstallNuGetPackage(string packageSource, string nugetPackage)
        {
            if (!PackageInstallerServices.IsPackageInstalled(this.Project, nugetPackage))
            {
                Version packageVersion = null;
                PackageInstaller.InstallPackage(packageSource, this.Project, nugetPackage, packageVersion, false);

                await this.Context.Logger.WriteMessageAsync(LoggerMessageCategory.Information, $"Nuget Package \"{nugetPackage}\" for OData V3 was added.");
            }
            else
            {
                await this.Context.Logger.WriteMessageAsync(LoggerMessageCategory.Information, $"Nuget Package \"{nugetPackage}\" for OData V3 already installed.");
            }
        }

        public override async Task AddGeneratedClientCode()
        {
            await this.Context.Logger.WriteMessageAsync(LoggerMessageCategory.Information, "Generating Client Proxy for OData V3...");

            var generator = new EntityClassGenerator(LanguageOption.GenerateCSharpCode)
            {
                UseDataServiceCollection = this.ServiceConfiguration.UseDataServiceCollection,
                Version = DataServiceCodeVersion.V3
            };

            // Set up XML secure resolver
            var xmlUrlResolver = new XmlUrlResolver()
            {
                Credentials = System.Net.CredentialCache.DefaultNetworkCredentials
            };

            var permissionSet = new PermissionSet(System.Security.Permissions.PermissionState.Unrestricted);

            var settings = new XmlReaderSettings()
            {
                XmlResolver = new XmlSecureResolver(xmlUrlResolver, permissionSet)
            };

            using (var reader = XmlReader.Create(this.MetadataUri, settings))
            {
                var tempFile = Path.GetTempFileName();
                var noErrors = true;

                using (var writer = File.CreateText(tempFile))
                {
                    var errors = generator.GenerateCode(reader, writer, this.ServiceConfiguration.NamespacePrefix);
                    await writer.FlushAsync();
                    if (errors != null && errors.Any())
                    {
                        noErrors = false;

                        foreach (var err in errors)
                        {
                            await this.Context.Logger.WriteMessageAsync(LoggerMessageCategory.Warning, err.Message);
                        }

                        await this.Context.Logger.WriteMessageAsync(LoggerMessageCategory.Warning, "Client Proxy for OData V3 was not generated.");
                    }
                }

                if (noErrors)
                {
                    var outputFile = Path.Combine(GetReferenceFileFolder(), this.GeneratedFileNamePrefix + ".cs");
                    await this.Context.HandlerHelper.AddFileAsync(tempFile, outputFile);

                    await this.Context.Logger.WriteMessageAsync(LoggerMessageCategory.Information, "Client Proxy for OData V3 was generated.");

                    if (ServiceConfiguration.IncludeExtensionsT4File)
                        await AddGeneratedClientExtensionsCode(outputFile);
                }
            }
        }

        internal async Task AddGeneratedClientExtensionsCode(string generatedFileName)
        {
            if (!File.Exists(generatedFileName))
            {
                await this.Context.Logger.WriteMessageAsync(LoggerMessageCategory.Warning, "Client Proxy for OData V3 was not generated.");
                return;
            }

            await this.Context.Logger.WriteMessageAsync(LoggerMessageCategory.Information, "Generating Client Proxy extensions class for OData V3...");

            var tempFile = Path.GetTempFileName();
            var t4Folder = Path.Combine(CurrentAssemblyPath, "Templates");

            var proxyClassText = File.ReadAllText(generatedFileName);
            var proxyClassNamespace = Regex.Match(proxyClassText, @"(namespace\s)\w+", RegexOptions.IgnoreCase | RegexOptions.Multiline).Value.Trim().Replace("namespace ", "").Trim();
            var proxyClassName = Regex.Match(proxyClassText, @"(public partial class\s)\w+", RegexOptions.IgnoreCase | RegexOptions.Multiline).Value.Trim().Replace("public partial class ", "").Trim();

            using (var writer = File.CreateText(tempFile))
            {
                var text = File.ReadAllText(Path.Combine(t4Folder, Common.Constants.V3ExtensionsT4FileName));
                text = Regex.Replace(text, "(namespace )\\$rootnamespace\\$", "$1" + (string.IsNullOrEmpty(ServiceConfiguration.NamespacePrefix) ? proxyClassNamespace : ServiceConfiguration.NamespacePrefix));
                text = Regex.Replace(text, "\\$proxyClassName\\$", proxyClassName);
                text = Regex.Replace(text, "(new Uri\\()\"\\$metadataDocumentUri\\$\"\\)\\)", "$1\"" + ServiceConfiguration.Endpoint.Replace("$metadata", "") + "\"))");

                await writer.WriteAsync(text);
                await writer.FlushAsync();
            }

            var outputFile = Path.Combine(GetReferenceFileFolder(), this.GeneratedFileNamePrefix + "Extensions.cs");
            await this.Context.HandlerHelper.AddFileAsync(tempFile, outputFile);

            await this.Context.Logger.WriteMessageAsync(LoggerMessageCategory.Information, "Client Proxy extensions class for OData V3 was generated.");
        }
    }
}