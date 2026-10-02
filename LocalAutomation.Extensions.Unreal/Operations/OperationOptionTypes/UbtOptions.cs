using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace LocalAutomation.Extensions.Unreal.Operations.OperationOptionTypes
{
    /// <summary>
    /// Provides command-line switches that control UnrealBuildTool compilation.
    /// </summary>
    [PersistedSettings("build")]
    public partial class UbtOptions : OperationOptions
    {
        public override int SortIndex => 25;

        /// <summary>
        /// Keeps the tool's acronym intact in the options heading.
        /// </summary>
        public override string Name => "UBT";

        [ObservableProperty]
        [property: DisplayName("No Hot Reload")]
        [property: Description("Disables UnrealBuildTool hot reload.")]
        private bool noHotReload = false;

        // Regeneration is opt-in because forcing UHT adds work even when generated headers are up to date.
        [ObservableProperty]
        [property: DisplayName("Force Header Generation")]
        [property: Description("Forces UnrealHeaderTool to regenerate headers, even when they appear up to date.")]
        private bool forceHeaderGeneration = false;
    }
}
