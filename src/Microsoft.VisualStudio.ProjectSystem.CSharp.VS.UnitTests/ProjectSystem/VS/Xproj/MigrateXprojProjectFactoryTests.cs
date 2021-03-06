﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.IO;
using Microsoft.VisualStudio.Packaging;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Xunit;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Xproj
{
    [ProjectSystemTrait]
    public class MigrateXprojProjectFactoryTests
    {
        private const string RootLocation = @"C:\Temp";
        private const string ProjectName = "XprojMigrationTests";
        private static readonly string XprojLocation = Path.Combine(RootLocation, $"{ProjectName}.xproj");
        private static readonly string XprojUserLocation = Path.Combine(RootLocation, $"{ProjectName}.xproj.user");
        private static readonly string ProjectJsonLocation = Path.Combine(RootLocation, "project.json");
        private static readonly string ProjectLockJsonLocation = Path.Combine(RootLocation, "project.lock.json");
        private static readonly string BackupLocation = Path.Combine(RootLocation, "Backup");
        private static readonly string CsprojLocation = Path.Combine(RootLocation, $"{ProjectName}.csproj");
        private static readonly string LogFileLocation = Path.Combine(RootLocation, "asdf.1234");
        private static readonly string MigrateCommand = $"dotnet migrate --skip-backup -s -x \"{XprojLocation}\" \"{RootLocation}\" -r \"{LogFileLocation}\" --format-report-file-json";

        [Fact]
        public void MigrateXprojProjectFactory_NullProcessRunner_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>("runner", () => new MigrateXprojProjectFactory(null, new IFileSystemMock()));
        }

        [Fact]
        public void MigrateXprojProjectFactory_NullFileSystem_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>("fileSystem", () => new MigrateXprojProjectFactory(ProcessRunnerFactory.CreateRunner(), null));
        }

        [Fact]
        public void MigrateXprojProjectFactory_ValidArgs_BackupsCorrectly()
        {
            var procRunner = ProcessRunnerFactory.CreateRunner();
            var fileSystem = CreateFileSystem();
            var migrator = new MigrateXprojProjectFactory(procRunner, fileSystem);

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);

            Assert.True(migrator.BackupProject(BackupLocation, XprojLocation, "XprojMigrationTests", logger));

            // We expect 2 informational messages about what files were backed up.
            Assert.Equal(2, loggedMessages.Count);
            loggedMessages.ForEach(message =>
            {
                Assert.Equal((uint)__VSUL_ERRORLEVEL.VSUL_INFORMATIONAL, message.Level);
                Assert.Equal("XprojMigrationTests", message.Project);
            });

            // The first message should be about the old xproj, the second about the project.json
            var xprojBackedUp = Path.Combine(BackupLocation, "XprojMigrationTests.xproj");
            var projectJsonBackedUp = Path.Combine(BackupLocation, "project.json");

            Assert.Equal(XprojLocation, loggedMessages[0].File);
            Assert.Equal(ProjectJsonLocation, loggedMessages[1].File);
            Assert.Equal(string.Format(VSResources.MigrationBackupFile, XprojLocation, xprojBackedUp), loggedMessages[0].Message);
            Assert.Equal(string.Format(VSResources.MigrationBackupFile, ProjectJsonLocation, projectJsonBackedUp), loggedMessages[1].Message);

            // Finally, assert that there actually are backup files in the backup directory
            var backedUpFiles = fileSystem.EnumerateFiles(BackupLocation, "*", SearchOption.TopDirectoryOnly);
            Assert.Equal(2, backedUpFiles.Count());
            Assert.True(backedUpFiles.Contains(xprojBackedUp));
            Assert.True(backedUpFiles.Contains(projectJsonBackedUp));
        }

        [Fact]
        public void MigrateXprojProjectFactory_WithXprojUser_BackupsCorrectly()
        {
            var procRunner = ProcessRunnerFactory.CreateRunner();
            var fileSystem = CreateFileSystem(withXprojUser: true, withProjectLock: true);
            var migrator = new MigrateXprojProjectFactory(procRunner, fileSystem);

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);

            Assert.True(migrator.BackupProject(BackupLocation, XprojLocation, "XprojMigrationTests", logger));

            // We expect 2 informational messages about what files were backed up.
            Assert.Equal(3, loggedMessages.Count);
            loggedMessages.ForEach(message =>
            {
                Assert.Equal((uint)__VSUL_ERRORLEVEL.VSUL_INFORMATIONAL, message.Level);
                Assert.Equal("XprojMigrationTests", message.Project);
            });

            // The first message should be about the old xproj, the second about the project.json
            var xprojBackedUp = Path.Combine(BackupLocation, "XprojMigrationTests.xproj");
            var xprojUserBackedUp = Path.Combine(BackupLocation, "XprojMigrationTests.xproj.user");
            var projectJsonBackedUp = Path.Combine(BackupLocation, "project.json");

            Assert.Equal(XprojLocation, loggedMessages[0].File);
            Assert.Equal(ProjectJsonLocation, loggedMessages[1].File);
            Assert.Equal(XprojUserLocation, loggedMessages[2].File);
            Assert.Equal(string.Format(VSResources.MigrationBackupFile, XprojLocation, xprojBackedUp), loggedMessages[0].Message);
            Assert.Equal(string.Format(VSResources.MigrationBackupFile, ProjectJsonLocation, projectJsonBackedUp), loggedMessages[1].Message);
            Assert.Equal(string.Format(VSResources.MigrationBackupFile, XprojUserLocation, xprojUserBackedUp), loggedMessages[2].Message);

            // Finally, assert that there actually are backup files in the backup directory
            var backedUpFiles = fileSystem.EnumerateFiles(BackupLocation, "*", SearchOption.TopDirectoryOnly);
            Assert.Equal(3, backedUpFiles.Count());
            Assert.True(backedUpFiles.Contains(xprojBackedUp));
            Assert.True(backedUpFiles.Contains(projectJsonBackedUp));
            Assert.True(backedUpFiles.Contains(xprojUserBackedUp));
        }


        [Fact]
        public void MigrateXprojProjectFactory_NonExistantProjectJson_DoesNotBackUp()
        {
            var procRunner = ProcessRunnerFactory.CreateRunner();
            var migrator = new MigrateXprojProjectFactory(procRunner, CreateFileSystem(false));

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);

            Assert.False(migrator.BackupProject(BackupLocation, XprojLocation, "XprojMigrationTests", logger));

            Assert.Equal(1, loggedMessages.Count);
            Assert.Equal((uint)__VSUL_ERRORLEVEL.VSUL_ERROR, loggedMessages[0].Level);
            Assert.Equal("XprojMigrationTests", loggedMessages[0].Project);
            Assert.Equal(ProjectJsonLocation, loggedMessages[0].File);
            Assert.Equal(string.Format(VSResources.XprojMigrationFailedProjectJsonFileNotFound, ProjectName, ProjectJsonLocation), loggedMessages[0].Message);
        }

        [Fact]
        public void MigrateXprojProjectFactory_ValidPaths_CallMigrateCorrectly()
        {
            // Runner returns valid response, standard exit code
            var procRunner = ProcessRunnerFactory.ImplementRunner(ProcessVerifier);
            var migrator = new MigrateXprojProjectFactory(procRunner, CreateFileSystem(false));

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);

            var migrateResults = migrator.MigrateProject(RootLocation, XprojLocation, "XprojMigrationTests", logger);

            Assert.Equal(0, migrateResults.exitCode);
            Assert.Equal(0, loggedMessages.Count);
        }

        [Fact]
        public void MigrateXprojProjectFactory_MigrateOutput_LoggedCorrectly()
        {
            // Runner returns valid response, standard exit code
            var procRunner = ProcessRunnerFactory.ImplementRunner(ProcessVerifier, outputText: "Standard Output", errorText: "Standard Error");
            var migrator = new MigrateXprojProjectFactory(procRunner, CreateFileSystem(false));

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);

            Assert.Equal(0, migrator.MigrateProject(RootLocation, XprojLocation, "XprojMigrationTests", logger).exitCode);
            Assert.Equal(2, loggedMessages.Count);
            Assert.Equal(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_INFORMATIONAL,
                Message = "Standard Output",
                Project = "XprojMigrationTests"
            }, loggedMessages[0]);
            Assert.Equal(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_WARNING,
                Message = "Standard Error",
                Project = "XprojMigrationTests"
            }, loggedMessages[1]);
        }

        [Fact]
        public void MigrateXprojProjectFactory_MigrateError_ReturnsErrorCode()
        {
            // Runner returns valid response, standard exit code
            var procRunner = ProcessRunnerFactory.ImplementRunner(ProcessVerifier, exitCode: VSConstants.E_FAIL);
            var migrator = new MigrateXprojProjectFactory(procRunner, CreateFileSystem(false));

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);
            var migrateResults = migrator.MigrateProject(RootLocation, XprojLocation, "XprojMigrationTests", logger);
            Assert.Equal(VSConstants.E_FAIL, migrateResults.exitCode);
            Assert.Equal(LogFileLocation, migrateResults.logFile);
        }

        [Fact]
        public void MigrateXprojProjectFactory_ValidCsproj_FindsCsproj()
        {
            var fileSystem = CreateFileSystem();

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);
            var migrator = new MigrateXprojProjectFactory(ProcessRunnerFactory.CreateRunner(), fileSystem);

            var (foundProjectFile, success) = migrator.LogReport(LogFileLocation, 0, ProjectName, XprojLocation, logger);
            Assert.Equal(CsprojLocation, foundProjectFile);
            Assert.True(success);
            Assert.Equal(0, loggedMessages.Count);
        }

        [Fact]
        public void MigrateXprojProjectFactory_NoLogFile_LogsError()
        {
            var fileSystem = CreateFileSystem(withEntries: false);

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);
            var migrator = new MigrateXprojProjectFactory(ProcessRunnerFactory.CreateRunner(), fileSystem);

            var (projectFile, success) = migrator.LogReport(LogFileLocation, VSConstants.E_FAIL, ProjectName, XprojLocation, logger);
            Assert.Equal(string.Empty, projectFile);
            Assert.False(success);
            Assert.Equal(2, loggedMessages.Count);
            Assert.Equal(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_ERROR,
                Message = string.Format(VSResources.XprojMigrationFailedCannotReadReport, LogFileLocation),
                Project = ProjectName
            }, loggedMessages[0]);
            Assert.Equal(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_ERROR,
                Message = string.Format(VSResources.XprojMigrationGeneralFailure, ProjectName, MigrateCommand, VSConstants.E_FAIL),
                Project = ProjectName
            }, loggedMessages[1]);
        }

        [Fact]
        public void MigrateXprojProjectFactory_InvalidLogFile_LogsError()
        {
            var fileSystem = CreateFileSystem(withEntries: false);
            fileSystem.WriteAllText(LogFileLocation, string.Empty);

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);
            var migrator = new MigrateXprojProjectFactory(ProcessRunnerFactory.CreateRunner(), fileSystem);

            var (projectFile, success) = migrator.LogReport(LogFileLocation, VSConstants.E_ABORT, ProjectName, XprojLocation, logger);
            Assert.Equal(string.Empty, projectFile);
            Assert.False(success);
            Assert.Equal(2, loggedMessages.Count);
            Assert.Equal(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_ERROR,
                Message = string.Format(VSResources.XprojMigrationFailedCannotReadReport, LogFileLocation),
                Project = ProjectName
            }, loggedMessages[0]);
            Assert.Equal(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_ERROR,
                Message = string.Format(VSResources.XprojMigrationGeneralFailure, ProjectName, MigrateCommand, VSConstants.E_ABORT),
                Project = ProjectName
            }, loggedMessages[1]);
        }

        [Fact]
        public void MigrateXprojProjectFactory_LogWithErrors_MessagesAreLoggedToVs()
        {
            var fileSystem = CreateFileSystem(withEntries: false);
            var migrationReport = new MigrationReport(0, new List<ProjectMigrationReport>
            {
                new ProjectMigrationReport(false, string.Empty, RootLocation, ProjectName, new List<string>
                {
                    "Sample Warning 1",
                    "Sample Warning 2"
                }, new List<MigrationError> {
                    new MigrationError("asdf1234", "General Error", "An error has occurred"),
                    new MigrationError("fdsa4321", "Specific Error", "A different error has occurred")
                })
            });
            fileSystem.WriteAllText(LogFileLocation, JsonConvert.SerializeObject(migrationReport));

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);
            var migrator = new MigrateXprojProjectFactory(ProcessRunnerFactory.CreateRunner(), fileSystem);

            var (projectFile, success) = migrator.LogReport(LogFileLocation, VSConstants.E_ABORT, ProjectName, XprojLocation, logger);
            Assert.Equal(string.Empty, projectFile);
            Assert.False(success);
            Assert.Equal(5, loggedMessages.Count);
            Assert.Contains(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_WARNING,
                Message = "Sample Warning 1",
                Project = ProjectName
            }, loggedMessages);
            Assert.Contains(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_WARNING,
                Message = "Sample Warning 2",
                Project = ProjectName
            }, loggedMessages);
            Assert.Contains(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_ERROR,
                Message = "asdf1234::General Error: An error has occurred",
                Project = ProjectName
            }, loggedMessages);
            Assert.Contains(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_ERROR,
                Message = "fdsa4321::Specific Error: A different error has occurred",
                Project = ProjectName
            }, loggedMessages);
            Assert.Contains(new LogMessage
            {
                File = XprojLocation,
                Level = (uint)__VSUL_ERRORLEVEL.VSUL_ERROR,
                Message = string.Format(VSResources.XprojMigrationGeneralFailure, ProjectName, MigrateCommand, VSConstants.E_ABORT),
                Project = ProjectName
            }, loggedMessages);
        }

        [Fact]
        public void MigrateXprojProjectFactory_ValidProject_IsUpgradable()
        {
            var fileSystem = CreateFileSystem();

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);
            var migrator = new MigrateXprojProjectFactory(ProcessRunnerFactory.CreateRunner(), fileSystem);

            Assert.Equal(VSConstants.S_OK,
                migrator.UpgradeProject_CheckOnly(XprojLocation, logger, out int upgradeRequired, out Guid newProjectFactory, out uint capabilityFlags));
            Assert.Equal((int)__VSPPROJECTUPGRADEVIAFACTORYREPAIRFLAGS.VSPUVF_PROJECT_ONEWAYUPGRADE, upgradeRequired);
            Assert.Equal(Guid.Parse(CSharpProjectSystemPackage.ProjectTypeGuid), newProjectFactory);
            Assert.Equal((uint)(__VSPPROJECTUPGRADEVIAFACTORYFLAGS.PUVFF_BACKUPSUPPORTED | __VSPPROJECTUPGRADEVIAFACTORYFLAGS.PUVFF_COPYBACKUP),
                capabilityFlags);
        }

        [Fact]
        public void MigrateXprojProjectFactory_Cleanup_RemovesAllFiles()
        {
            var fileSystem = CreateFileSystem(withEntries: true, withXprojUser: true, withProjectLock: true);

            var migrator = new MigrateXprojProjectFactory(ProcessRunnerFactory.CreateRunner(), fileSystem);

            migrator.CleanupXproj(RootLocation, ProjectName);
            Assert.False(fileSystem.FileExists(XprojLocation));
            Assert.False(fileSystem.FileExists(ProjectJsonLocation));
            Assert.False(fileSystem.FileExists(XprojUserLocation));
            Assert.False(fileSystem.FileExists(ProjectLockJsonLocation));
        }

        [Fact]
        public void MigrateXprojProjectFactory_NoXprojUserOrProjectLock_CausesNoIssues()
        {
            var fileSystem = CreateFileSystem(withEntries: true, withXprojUser: false, withProjectLock: false);

            var migrator = new MigrateXprojProjectFactory(ProcessRunnerFactory.CreateRunner(), fileSystem);

            migrator.CleanupXproj(RootLocation, ProjectName);
            Assert.False(fileSystem.FileExists(XprojLocation));
            Assert.False(fileSystem.FileExists(ProjectJsonLocation));
        }

        [Fact]
        public void MigrateXprojProjectFactory_E2E_Works()
        {
            var fileSystem = CreateFileSystem(withEntries: true, withXprojUser: true, withProjectLock: true);
            var processRunner = ProcessRunnerFactory.ImplementRunner(pInfo =>
            {
                ProcessVerifier(pInfo);
                fileSystem.Create(CsprojLocation);
            });

            var loggedMessages = new List<LogMessage>();
            var logger = IVsUpgradeLoggerFactory.CreateLogger(loggedMessages);

            var migrator = new MigrateXprojProjectFactory(processRunner, fileSystem);

            Assert.Equal(VSConstants.S_OK, migrator.UpgradeProject(XprojLocation, 0, BackupLocation, out string outCsproj, logger, out int upgradeRequired, out Guid newProjectFactory));
            Assert.True(fileSystem.FileExists(CsprojLocation));
            Assert.True(fileSystem.FileExists(Path.Combine(BackupLocation, $"{ProjectName}.xproj")));
            Assert.True(fileSystem.FileExists(Path.Combine(BackupLocation, "project.json")));
            Assert.True(fileSystem.FileExists(Path.Combine(BackupLocation, $"{ProjectName}.xproj.user")));
            Assert.False(fileSystem.FileExists(XprojLocation));
            Assert.False(fileSystem.FileExists(ProjectJsonLocation));
            Assert.False(fileSystem.FileExists(XprojUserLocation));
            Assert.False(fileSystem.FileExists(ProjectLockJsonLocation));
            Assert.Equal(CsprojLocation, outCsproj);
            Assert.Equal((int)__VSPPROJECTUPGRADEVIAFACTORYREPAIRFLAGS.VSPUVF_PROJECT_ONEWAYUPGRADE, upgradeRequired);
            Assert.Equal(Guid.Parse(CSharpProjectSystemPackage.ProjectTypeGuid), newProjectFactory);
        }

        private void ProcessVerifier(ProcessStartInfo info)
        {
            Assert.Equal("dotnet.exe", info.FileName);
            Assert.Equal($"migrate --skip-backup -s -x \"{XprojLocation}\" \"{RootLocation}\" -r \"{LogFileLocation}\" --format-report-file-json", info.Arguments);
            Assert.True(info.EnvironmentVariables.ContainsKey("DOTNET_SKIP_FIRST_TIME_EXPERIENCE"));
            Assert.Equal("true", info.EnvironmentVariables["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]);
        }

        private IFileSystem CreateFileSystem(bool withEntries = true, MigrationReport report = null, bool withXprojUser = false, bool withProjectLock = false)
        {
            var fileSystem = new IFileSystemMock();
            if (withEntries)
            {
                fileSystem.CreateDirectory(RootLocation);
                fileSystem.CreateDirectory(BackupLocation);
                fileSystem.Create(XprojLocation);
                fileSystem.Create(ProjectJsonLocation);
                if (withXprojUser)
                {
                    fileSystem.Create(XprojUserLocation);
                }

                if (withProjectLock)
                {
                    fileSystem.Create(ProjectLockJsonLocation);
                }
            }

            fileSystem.SetTempFile(LogFileLocation);

            if (withEntries)
            {
                report = report ?? new MigrationReport(1, new List<ProjectMigrationReport>()
                {
                    new ProjectMigrationReport(true, CsprojLocation, RootLocation, ProjectName, new List<string>(), new List<MigrationError>())
                });
                fileSystem.WriteAllText(LogFileLocation, JsonConvert.SerializeObject(report));
            }

            return fileSystem;
        }
    }
}
