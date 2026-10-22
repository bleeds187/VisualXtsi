using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Options;
using Task = System.Threading.Tasks.Task;

namespace VisualXtsi
{
    /// <summary>
    /// The package exists only to register the options page; the editor features are MEF
    /// components and do not need it loaded.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuid)]
    [ProvideOptionPage(typeof(OptionsPage), "VisualXtsi", "General", 0, 0, true)]
    public sealed class VisualXtsiPackage : AsyncPackage
    {
        public const string PackageGuid = "b9a4e6d2-7c31-4f8e-a5d0-3e6c1f9b2a87";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            VxSettings.EnsureLoaded();
        }
    }
}
