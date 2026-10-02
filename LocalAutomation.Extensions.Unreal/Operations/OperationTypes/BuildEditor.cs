using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Extensions.Unreal.Operations.BaseOperations;
using SB.UnrealUtilities;

namespace LocalAutomation.Extensions.Unreal.Operations.OperationTypes
{
    [Operation(SortOrder = 2)]
    public class BuildEditor : BuildCookRunProjectOperationBase
    {
        /// <summary>
        /// Editor-only BuildCookRun requests expose configuration but no UBT build options because UAT does not
        /// forward ubtargs to editor targets.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(OperationOptionTypes.BuildConfigurationOptions)
                });
        }

        /// <summary>
        /// Editor prebuilds are modeled as the build-only BuildCookRun preset used by packaging workflows to prepare code
        /// outputs without entering cook or package phases.
        /// </summary>
        protected override BuildCookRunProjectRequest GetBuildCookRunRequest(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            return UATArguments.CreateEditorBuildRequest(
                operationParameters.GetOptions<OperationOptionTypes.BuildConfigurationOptions>().Configuration);
        }

        protected override string GetOperationName()
        {
            return "Build Editor";
        }

        /// <summary>
        /// Rejects unsupported build configurations before command generation so the user sees a clear validation
        /// message instead of a misleading UAT invocation.
        /// </summary>
        protected override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            BuildConfiguration configuration = operationParameters.GetOptions<OperationOptionTypes.BuildConfigurationOptions>().Configuration;
            try
            {
                UATArguments.CreateEditorBuildRequest(configuration);
                return null;
            }
            catch (InvalidOperationException exception)
            {
                // Surface the reusable preset's configuration restriction through runtime validation.
                return exception.Message;
            }
        }
    }
}
