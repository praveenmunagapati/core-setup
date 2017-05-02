// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.Build.Framework;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Build.Construction;
using System.Net.Http;
using System.Xml;
using System.Globalization;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using System.Threading;

namespace Microsoft.DotNet.Build.Tasks
{
    public partial class FinalizeBuild : Utility.AzureConnectionStringBuildTask
    {
        [Required]
        public string SemaphoreBlob { get; set; }
        [Required]
        public string FinalizeContainer { get; set; }
        public string MaxWait { get; set; }
        public string Delay { get; set; }
        [Required]
        public string ContainerName { get; set; }
        [Required]
        public string Channel { get; set; }
        [Required]
        public string Version { get; set; }
        [Required]
        public ITaskItem [] PublishRids { get; set; }
        public string CommitHash { get; set; }
        public bool ForcePublish { get; set; }

        private Regex _versionRegex = new Regex(@"(?<version>\d+\.\d+\.\d+)(-(?<prerelease>[^-]+-)?(?<major>\d+)-(?<minor>\d+))?");

        public override bool Execute()
        {
            ParseConnectionString();

            if (Log.HasLoggedErrors)
            {
                return false;
            }

            if (!FinalizeContainer.EndsWith("/"))
            {
                FinalizeContainer = $"{FinalizeContainer}/";
            }
            string targetVersionFile = $"{FinalizeContainer}{Version}";

            CreateBlobIfNotExists(SemaphoreBlob);

            AzureBlobLease blobLease = new AzureBlobLease(AccountName, AccountKey, ConnectionString, ContainerName, SemaphoreBlob, Log);
            Log.LogMessage($"Acquiring lease on semaphore blob '{SemaphoreBlob}'");
            blobLease.Acquire();

            // Prevent race conditions by dropping a version hint of what version this is. If we see this file
            // and it is the same as our version then we know that a race happened where two+ builds finished 
            // at the same time and someone already took care of publishing and we have no work to do.
            if (IsLatestSpecifiedVersion(targetVersionFile) && !ForcePublish)
            {
                Log.LogMessage(MessageImportance.Low, $"version hint file for publishing finalization is {targetVersionFile}");
                Log.LogMessage(MessageImportance.High, $"Version '{Version}' is already published, skipping finalization.");
                Log.LogMessage($"Releasing lease on semaphore blob '{SemaphoreBlob}'");
                blobLease.Release();
                return true;
            }
            else
            {

                // Delete old version files
                GetBlobList(FinalizeContainer)
                    .Select(s => s.Replace("/dotnet/", ""))
                    .Where(w => _versionRegex.Replace(Path.GetFileName(w), "") == "")
                    .ToList()
                    .ForEach(f => TryDeleteBlob(f));

                
                // Drop the version file signaling such for any race-condition builds (see above comment).
                CreateBlobIfNotExists(targetVersionFile);

                try
                {
                    CopyBlobs($"{Channel}/Binaries/{Version}", $"{Channel}/Binaries/Latest/");

                    CopyBlobs($"{Channel}/Installers/{Version}", $"{Channel}/Installers/Latest/");

                    // Generate the Sharedfx Version text files
                    List<string> versionFiles = PublishRids.Select(p => $"{p.ItemSpec}.version").ToList();

                    string sfxVersion = GetSharedFrameworkVersionFileContent();
                    foreach(string version in versionFiles)
                    {
                        PublishStringToBlob(ContainerName, $"{Channel}/dnvm/latest.sharedfx.{version}", sfxVersion);
                    }
                }
                finally
                {
                    blobLease.Release();
                }
            }
            return !Log.HasLoggedErrors;
        }

        private string GetSharedFrameworkVersionFileContent()
        {
            string returnString = string.Empty;
            if(!string.IsNullOrWhiteSpace(CommitHash))
            {
                returnString += $"{CommitHash}{Environment.NewLine}";
            }
            returnString += $"{Version}{Environment.NewLine}";
            return returnString;
        }

        public bool CopyBlobs(string sourceFolder, string destinationFolder)
        {
            bool returnStatus = true;
            List<Task<bool>> copyTasks = new List<Task<bool>>();
            string[] blobs = GetBlobList(sourceFolder);
            foreach (string blob in blobs)
            {
                string targetName = _versionRegex.Replace(Path.GetFileName(blob), "Latest");
                string sourceBlob = blob.Replace($"/{ContainerName}/", "");
                string destinationBlob = $"{destinationFolder}{targetName}";
                Log.LogMessage($"Copying blob '{sourceBlob}' to '{destinationBlob}'");
                copyTasks.Add(CopyBlobAsync(sourceBlob, destinationBlob));
            }
            Task.WaitAll(copyTasks.ToArray());
            copyTasks.ForEach(c => returnStatus &= c.Result);
            return returnStatus;
        }

        public bool TryDeleteBlob(string path)
        {
            return DeleteBlob(ContainerName, path);
        }

        public void CreateBlobIfNotExists(string path)
        {
            var blobList = GetBlobList(path);
            if(blobList.Count() == 0)
            {
                PublishStringToBlob(ContainerName, path, DateTime.Now.ToString());
            }
        }

        public bool IsLatestSpecifiedVersion(string versionFile)
        {
            var blobList = GetBlobList(versionFile);
            return blobList.Count() != 0;
        }

        public bool DeleteBlob(string container, string blob)
        {
            return DeleteAzureBlob.Execute(AccountName, 
                                           AccountKey, 
                                            ConnectionString, 
                                            container, 
                                            blob, 
                                            BuildEngine, 
                                            HostObject);
        }

        public Task<bool> CopyBlobAsync(string sourceBlobName, string destinationBlobName)
        {
            return CopyAzureBlobToBlob.ExecuteAsync(AccountName,
                                               AccountKey,
                                               ConnectionString,
                                               ContainerName,
                                               sourceBlobName,
                                               destinationBlobName,
                                               BuildEngine,
                                               HostObject);
        }

        public string[] GetBlobList(string path)
        {
            return GetAzureBlobList.Execute(AccountName,
                                            AccountKey,
                                            ConnectionString,
                                            ContainerName,
                                            path,
                                            BuildEngine,
                                            HostObject);
        }

        public bool PublishStringToBlob(string container, string blob, string contents)
        {
            return PublishStringToAzureBlob.Execute(AccountName, 
                                                    AccountKey, 
                                                    ConnectionString, 
                                                    container, 
                                                    blob, 
                                                    contents, 
                                                    BuildEngine, 
                                                    HostObject);
        }
    }
}