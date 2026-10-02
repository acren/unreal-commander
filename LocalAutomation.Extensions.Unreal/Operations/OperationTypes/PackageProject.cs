using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Extensions.Unreal.Operations.BaseOperations;
using LocalAutomation.Extensions.Unreal.Operations.OperationOptionTypes;
using SB.UnrealUtilities;

namespace LocalAutomation.Extensions.Unreal.Operations.OperationTypes
{
    [Operation(SortOrder = 6)]
    public class PackageProject : BuildCookRunProjectOperationBase
    {
        /// <summary>
        /// Project packaging exposes archive and cooker settings plus build options for its game/server compilation,
        /// which receives UAT's forwarded UBT arguments.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(BuildConfigurationOptions),
                    typeof(UbtOptions),
                    typeof(PackageOptions),
                    typeof(CookOptions)
                });
        }

        /// <summary>
        /// Binds package and cooker options to the reusable full-project packaging preset.
        /// </summary>
        protected override BuildCookRunProjectRequest GetBuildCookRunRequest(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            PackageOptions packageOptions = operationParameters.GetOptions<PackageOptions>();
            CookOptions cookOptions = operationParameters.GetOptions<CookOptions>();
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);

            return UATArguments.CreateProjectPackageRequest(
                engine,
                configuration: operationParameters.GetOptions<BuildConfigurationOptions>().Configuration,
                build: packageOptions.Build,
                noDebugInfo: packageOptions.NoDebugInfo,
                archiveDirectory: packageOptions.Archive ? GetOutputPath(operationParameters) : null,
                cookerConfiguration: cookOptions.CookerConfiguration,
                waitForAttach: cookOptions.WaitForAttach);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
