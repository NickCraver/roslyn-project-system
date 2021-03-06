﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.ProjectSystem.Utilities;
using Microsoft.VisualStudio.ProjectSystem.VS.UI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.VisualStudio.ProjectSystem.VS
{
    /// <summary>
    /// ProjectReloadManager
    ///
    /// VS wide CPS component which manages project reloads on behalf of projects. As projects are loaded they register with this component
    /// which will add that project to its list of watched projects. When the project file changes outside of VS, this object will call into the project to
    /// perform a silent reload. Using a central object allows batching of reload messages, especially when silent reloads fail due to problems with
    /// the project file, or the file in memory is dirty requiring user intervention. It also permits handling scenarios such as automatically reloading the
    /// projects which fail the solution level reload, the next time the project file changes.
    /// </summary>
    [Export(typeof(IProjectReloadManager))]
    [AppliesTo(ProjectCapability.HandlesOwnReload)]
    internal class ProjectReloadManager : OnceInitializedOnceDisposedAsync, IProjectReloadManager, IVsFileChangeEvents, 
                                          IVsSolutionEvents, IVsSolutionEvents4
    {


        [ImportingConstructor]
        public ProjectReloadManager(IProjectThreadingService threadHandling, [Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider, IUserNotificationServices userNotificationServices,
                                    IDialogServices dialogServices)
            : base(threadHandling.JoinableTaskContext)
        {
            _threadHandling = threadHandling;
            _serviceProvider = serviceProvider;
            _userNotificationServices = userNotificationServices;
            _dialogServices = dialogServices;
        }

        private readonly IServiceProvider _serviceProvider;
        private readonly IProjectThreadingService _threadHandling;
        private readonly IUserNotificationServices _userNotificationServices;
        private readonly IDialogServices _dialogServices;

        public uint  SolutionEventsCookie { get; private set; } = VSConstants.VSCOOKIE_NIL;
        public ITaskDelayScheduler ReloadDelayScheduler { get; private set; }

        private const int ReloadDelay = 1000;   // delay 1s before applying updated project contents.

        // Tracks the set of reloadable projects. The value is the file system watcher cookie
        private Dictionary<IReloadableProject, uint> _registeredProjects = new Dictionary<IReloadableProject, uint>();

        public IReadOnlyDictionary<IReloadableProject, uint> RegisteredProjects { get { return _registeredProjects;} }

        // Tracks the list of projects that have changed and need to be processed
        private List<IReloadableProject> _changedProjects = new List<IReloadableProject>();        
        
        /// <summary>
        /// Initialization occurs when the first project registers
        /// </summary>
        public Task Initialize()
        {
            return InitializeAsync();
        }

        /// <summary>
        /// Called by reloadable projects to register themselves
        /// </summary>
        public async Task RegisterProjectAsync(IReloadableProject project)
        {
            Requires.NotNull(project, nameof(project));

            await Initialize().ConfigureAwait(false);
            await _threadHandling.SwitchToUIThread();

            RegisterProject(project);
        }

        private void RegisterProject(IReloadableProject project)
        {
            uint filechangeCookie;
            lock(_registeredProjects)
            {
                _registeredProjects.TryGetValue(project, out filechangeCookie);
            }

            System.Diagnostics.Debug.Assert(filechangeCookie == VSConstants.VSCOOKIE_NIL);
            if (filechangeCookie == VSConstants.VSCOOKIE_NIL)
            {
                IVsFileChangeEx fileChangeService = _serviceProvider.GetService<IVsFileChangeEx, SVsFileChangeEx>();
                if (fileChangeService != null)
                {
                    int hr = fileChangeService.AdviseFileChange(project.ProjectFile, (uint)(_VSFILECHANGEFLAGS.VSFILECHG_Time | _VSFILECHANGEFLAGS.VSFILECHG_Size), this, out filechangeCookie);
                    System.Diagnostics.Debug.Assert(ErrorHandler.Succeeded(hr) && filechangeCookie != VSConstants.VSCOOKIE_NIL);
                    if (ErrorHandler.Succeeded(hr) && filechangeCookie != VSConstants.VSCOOKIE_NIL)
                    {
                        lock(_registeredProjects)
                        {
                            _registeredProjects.Add(project, filechangeCookie);
                        }
                    }
                    else
                    {
                        throw new COMException(string.Format(VSResources.FailedToWatchProject, project.ProjectFile), hr);
                    }
                }
            }
        }

        /// <summary>
        /// Called by reloadable projects upon close go unregister themselves
        /// Removes the file change watch on the project file. 
        /// </summary>
        public async Task UnregisterProjectAsync(IReloadableProject project)
        {
            Requires.NotNull(project, nameof(project));

            await _threadHandling.SwitchToUIThread();
            
            UnregisterProject(project);
        }

        private void UnregisterProject(IReloadableProject project)
        {
            uint filechangeCookie;
            lock(_registeredProjects)
            {
                _registeredProjects.TryGetValue(project, out filechangeCookie);
            }

            if (filechangeCookie !=  VSConstants.VSCOOKIE_NIL)
            {
                // Remove watch
                IVsFileChangeEx fileChangeService = _serviceProvider.GetService<IVsFileChangeEx, SVsFileChangeEx>();
                if (fileChangeService != null)
                {
                    int hr = fileChangeService.UnadviseFileChange(filechangeCookie);
                    System.Diagnostics.Debug.Assert(ErrorHandler.Succeeded(hr));
                }

                // Always remove the watcher from our list
                lock (_registeredProjects)
                {
                    _registeredProjects.Remove(project);
                }
            }
        }

        /// <summary>
        /// Adds a file change watcher on the project file.
        /// </summary>
        private async Task ConnectToSolutionEvents()
        {
            await _threadHandling.SwitchToUIThread();

            if (SolutionEventsCookie == VSConstants.VSCOOKIE_NIL)
            {
                IVsSolution solution = _serviceProvider.GetService<IVsSolution, SVsSolution>();
                if (solution != null)
                {
                    int hr = solution.AdviseSolutionEvents(this, out uint cookie);
                    SolutionEventsCookie = cookie;
                    System.Diagnostics.Debug.Assert(ErrorHandler.Succeeded(hr) && SolutionEventsCookie != VSConstants.VSCOOKIE_NIL);
                    ErrorHandler.ThrowOnFailure(hr);
                }
            }
        }

        /// <summary>
        /// Removes the file change watch on the project file. 
        /// </summary>
        private async Task DisconnectFromSolutionEvents()
        {
            await _threadHandling.SwitchToUIThread();

            if (SolutionEventsCookie != VSConstants.VSCOOKIE_NIL)
            {
                IVsSolution solution = _serviceProvider.GetService<IVsSolution, SVsSolution>();
                if (solution != null)
                {
                    int hr = solution.UnadviseSolutionEvents(SolutionEventsCookie);
                    System.Diagnostics.Debug.Assert(ErrorHandler.Succeeded(hr));
                    SolutionEventsCookie = VSConstants.VSCOOKIE_NIL;
                }
            }
        }

        /// <summary>
        /// Handles one time initialization
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            ReloadDelayScheduler = new TaskDelayScheduler(TimeSpan.FromMilliseconds(ReloadDelay), _threadHandling, CancellationToken.None);
            await ConnectToSolutionEvents().ConfigureAwait(false);

        }

        /// <summary>
        /// IDisposable handler. Should only be called once
        /// </summary>
        protected override async Task DisposeCoreAsync(bool initialized)
        {
            if (ReloadDelayScheduler != null)
            {
                ReloadDelayScheduler.Dispose();
                ReloadDelayScheduler = null;
            }
            await DisconnectFromSolutionEvents().ConfigureAwait(false);
        }

        /// <summary>
        /// Called when one of the project files changes. In our case since only one file is watched with each cookie so the list of files
        /// should be one.
        /// </summary>
        public int FilesChanged(uint cChanges, string[] rgpszFile, uint[] grfChange)
        {
            if (cChanges == 1 && (grfChange[0] & (uint)(_VSFILECHANGEFLAGS.VSFILECHG_Size | _VSFILECHANGEFLAGS.VSFILECHG_Time)) != 0)
            {
                IReloadableProject changedProject = null;
                lock(_registeredProjects)
                {
                    changedProject = _registeredProjects.FirstOrDefault(kv => kv.Key.ProjectFile.Equals(rgpszFile[0], StringComparison.OrdinalIgnoreCase)).Key;
                }

                if (changedProject != null)
                {
                    lock(_changedProjects)
                    {
                        if (!_changedProjects.Contains(changedProject))
                        {
                            _changedProjects.Add(changedProject);
                        }

                        ReloadDelayScheduler.ScheduleAsyncTask(async (ct) => 
                        {
                            // Grab the UI thread so that we block until the reload of this set of 
                            // projects completes.
                            await _threadHandling.SwitchToUIThread();

                            if (ct.IsCancellationRequested)
                            {
                                return;
                            }

                            // Get the list of projects and create a new empty list to put new requests
                            List<IReloadableProject> changedProjects;
                            lock (_changedProjects)
                            {
                                changedProjects = _changedProjects;
                                _changedProjects = new List<IReloadableProject>();
                            }
                               
                            var failedProjects = new List<Tuple<IReloadableProject, ProjectReloadResult>>();

                            foreach (var project in changedProjects)
                            {
                                ProjectReloadResult result =  await project.ReloadProjectAsync().ConfigureAwait(true);

                                if (result == ProjectReloadResult.ReloadFailed || result == ProjectReloadResult.ReloadFailedProjectDirty)
                                {
                                    failedProjects.Add(new Tuple<IReloadableProject, ProjectReloadResult>(project, result));
                                }
                            }
                            
                            ProcessProjectReloadFailures(failedProjects);

                        });
                    }
                }
            }
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Prompts once for all the projects 
        /// For each dirty project it prompts first to see what the user action needs to be and then
        /// reloads the project if desired. Other options are do nothing amd save as and then reload
        /// </summary>
        private void ProcessProjectReloadFailures(List<Tuple<IReloadableProject, ProjectReloadResult>> failedProjects)
        {
            if (failedProjects.Count == 0)
            {
                return;
            }

            // Process each project. if the project is dirty in memory we need to prompt for each action. Non-dirty ones are
            // simply reloaded
            bool ignoreAll = false;
            bool reloadAll = false;
            foreach (var failure in failedProjects)
            {
                if (failure.Item2 == ProjectReloadResult.ReloadFailedProjectDirty)
                {
                    ProcessProjectDirtyInMemory(failure.Item1);
                }
                else
                {
                    if (ignoreAll)
                    {   
                        // do nothing
                    }
                    else if (reloadAll)
                    {
                        ReloadProjectInSolution(failure.Item1);
                    }
                    else
                    {
                        ProcessProjectWhichFailedReload(failure.Item1, out ignoreAll, out reloadAll);
                    }
                }
            }
        }

        /// <summary>
        /// Puts up a prompt appropriate for project which failed the autoload. ignoreAll or reloadAll will be set
        /// to true if the user selected those options
        /// </summary>
        private void ProcessProjectWhichFailedReload(IReloadableProject project, out bool ignoreAll, out bool reloadAll)
        {
            ignoreAll = false;
            reloadAll = false;
            var buttons = new string[] {VSResources.IgnoreAll,
                                        VSResources.Ignore,
                                        VSResources.ReloadAll,
                                        VSResources.Reload};

            var msgText = string.Format(VSResources.ProjectModificationsPrompt, Path.GetFileNameWithoutExtension(project.ProjectFile));
            switch (_dialogServices.ShowMultiChoiceMsgBox(VSResources.ProjectModificationDlgTitle, msgText, buttons))
            {
                case MultiChoiceMsgBoxResult.Cancel:
                {
                    break;
                }
                case MultiChoiceMsgBoxResult.Button1:
                {
                    // Ignore All
                    ignoreAll = true;
                    break;
                }
                case MultiChoiceMsgBoxResult.Button2:
                {
                    // Ignore
                    break;
                }
                case MultiChoiceMsgBoxResult.Button3:
                {
                    // Reload  all
                    reloadAll = true;
                    ReloadProjectInSolution(project);
                    break;
                }
                case MultiChoiceMsgBoxResult.Button4:
                {
                    // Reload
                    ReloadProjectInSolution(project);
                    break;
                }
            }
        }

        /// <summary>
        /// Puts up UI to allow the user to decide on the appropriate action for a project which is dirty
        /// in memory
        /// </summary>
        private void ProcessProjectDirtyInMemory(IReloadableProject project)
        {
            var buttons = new string[] {VSResources.Ignore,
                                        VSResources.Overwrite,
                                        VSResources.Discard,
                                        VSResources.SaveAs};

            var msgText = string.Format(VSResources.ConflictingModificationsPrompt, Path.GetFileNameWithoutExtension(project.ProjectFile));

            switch (_dialogServices.ShowMultiChoiceMsgBox(VSResources.ConflictingProjectModificationTitle, msgText, buttons))
            {
                case MultiChoiceMsgBoxResult.Cancel:
                {
                    break;
                }
                case MultiChoiceMsgBoxResult.Button1:
                {
                    // Ignore
                    break;
                }
                case MultiChoiceMsgBoxResult.Button2:
                {
                    // Overwrite
                    int hr = SaveProject(project);

                    if (ErrorHandler.Failed(hr))
                    {
                        _userNotificationServices.ReportErrorInfo(hr);
                    }
                    break;
                }
                case MultiChoiceMsgBoxResult.Button3:
                {
                    // Discard 
                    ReloadProjectInSolution(project);
                    break;
                }
                case MultiChoiceMsgBoxResult.Button4:
                {
                    // Save as
                    int hr = SaveAsProject(project);
                    if (ErrorHandler.Succeeded(hr))
                    {
                        ReloadProjectInSolution(project);
                    }
                    else
                    {
                        _userNotificationServices.ReportErrorInfo(hr);
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Helper to use the solution to reload the project.
        /// Reloading is managed via the ReloadItem() method of our parent hierarchy (solution 
        /// or solution folder).  So first we get our parent hierarchy and our itemid in the parent
        /// hierarchy. 
        /// </summary>
        void ReloadProjectInSolution(IReloadableProject project)
        {
            // Get our parent hierarchy and our itemid in the parent hierarchy.
            IVsHierarchy parentHier = project.VsHierarchy.GetProperty<IVsHierarchy>(VsHierarchyPropID.ParentHierarchy, null);
            if (parentHier == null)
            {
                ErrorHandler.ThrowOnFailure(VSConstants.E_UNEXPECTED);
            }

            uint parentItemid  = (uint)project.VsHierarchy.GetProperty(VsHierarchyPropID.ParentHierarchyItemid, unchecked((int)VSConstants.VSITEMID_NIL));
            if (parentItemid == VSConstants.VSITEMID_NIL)
            {
                ErrorHandler.ThrowOnFailure(VSConstants.E_UNEXPECTED);
            }
            
            // Now using IVsPersistHierarchyItem2 we reload the project.
            int hr = ((IVsPersistHierarchyItem2)parentHier).ReloadItem((uint)parentItemid, dwReserved: 0);
            ErrorHandler.ThrowOnFailure(hr);
        }

        /// <summary>
        /// Saves just the project file (does not save dirty documents)
        /// </summary>
       int SaveProject(IReloadableProject project)
        {
            IVsSolution solution = _serviceProvider.GetService<IVsSolution, SVsSolution>();
            __VSSLNSAVEOPTIONS saveOpts = __VSSLNSAVEOPTIONS.SLNSAVEOPT_SkipDocs;
            VsShellUtilities.GetRDTDocumentInfo(_serviceProvider, project.ProjectFile, out IVsHierarchy hier, out uint itemid, out IVsPersistDocData docData, out uint docCookie);
            int hr = solution.SaveSolutionElement((uint)saveOpts, project.VsHierarchy, docCookie);
            return hr;
        }

        /// <summary>
        /// Uses SaveAs to save a copy of the project somewhere else.
        /// </summary>
        int SaveAsProject(IReloadableProject project)
        {
            // Save as needs to go through IPersistFileFormat
            var persistFileFmt = project.VsHierarchy as IPersistFileFormat;
            var uishell = _serviceProvider.GetService<IVsUIShell, SVsUIShell>();
            int hr = uishell.SaveDocDataToFile(VSSAVEFLAGS.VSSAVE_SaveCopyAs, persistFileFmt, project.ProjectFile, out string newFile, out int canceled);

            if (ErrorHandler.Succeeded(hr) && canceled == 1)
            {
                hr = VSConstants.E_ABORT;
            }

            return hr;
        }

        /// <summary>
        /// Callback for directory changes. Since we don't watch the folder there is nothing to do here
        /// </summary>
        public int DirectoryChanged(string pszDirectory)
        {
            return VSConstants.S_OK;
        }

        /// <summary>
        /// IVsSolutionEvents4. We only care about OnAfterRenameProject. If our project file is renamed we need to
        /// stop watching the old file and start watching the new file
        /// </summary>
        public int OnAfterRenameProject(IVsHierarchy pHierarchy)
        {
            lock(_registeredProjects)
            {
                var renamedProject = _registeredProjects.FirstOrDefault(kv => kv.Key.VsHierarchy.Equals(pHierarchy)).Key;
                if (renamedProject != null)
                {
                    UnregisterProject(renamedProject);
                    RegisterProject(renamedProject);

                }
            }
            return VSConstants.S_OK;
        }

        public int OnQueryChangeProjectParent(IVsHierarchy pHierarchy, IVsHierarchy pNewParentHier, ref int pfCancel)
        {
            pfCancel = 0;
            return VSConstants.S_OK;
        }

        public int OnAfterChangeProjectParent(IVsHierarchy pHierarchy)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterAsynchOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            return VSConstants.S_OK;
        }

        /// <summary>
        /// IVsSolutionEvents memebers. Needed to implement this 
        /// </summary>
        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel)
        {
            pfCancel = 0;
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel)
        {
            pfCancel = 0;
            return VSConstants.S_OK;
        }

        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)
        {
            pfCancel = 0;
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseSolution(object pUnkReserved)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterCloseSolution(object pUnkReserved)
        {
            return VSConstants.S_OK;
        }
    }
}
