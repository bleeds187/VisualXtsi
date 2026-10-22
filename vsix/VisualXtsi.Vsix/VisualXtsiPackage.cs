using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Commands;
using VisualXtsi.Options;
using Task = System.Threading.Tasks.Task;

namespace VisualXtsi
{
    /// <summary>
    /// Registers the options page and the commands. The editor features are MEF components and
    /// do not need the package; Visual Studio loads it on first use of a page or command.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuid)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(OptionsPage), "VisualXtsi", "General", 0, 0, true)]
    public sealed class VisualXtsiPackage : AsyncPackage
    {
        public const string PackageGuid = "b9a4e6d2-7c31-4f8e-a5d0-3e6c1f9b2a87";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            VxSettings.EnsureLoaded();
            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
            {
                ToggleHeaderSourceCommand.Register(this, commands);
                OpenFileInSolutionCommand.Register(this, commands);
            }
            OpenFileInSolutionCommand.EnsureRoots();
        }
    }
}
