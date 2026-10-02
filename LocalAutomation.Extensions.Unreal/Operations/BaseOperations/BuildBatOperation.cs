using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Commands;
using LocalAutomation.Extensions.Unreal.Operations.OperationOptionTypes;
using LocalAutomation.Extensions.Unreal.Unreal;
using SB.SystemUtilities.Processes;
using SB.UnrealUtilities;
using RuntimeTarget = LocalAutomation.Runtime.OperationTarget;

namespace LocalAutomation.Extensions.Unreal.Operations.BaseOperations
{
    // Centralize the direct Build.bat wiring so all UBT-backed operations stay consistent.
    public class BuildBatOperation<T> : UnrealOperation<T> where T : RuntimeTarget
    {
        /// <summary>
        /// Composes direct Build.bat execution with Unreal argument and output policy.
        /// </summary>
        public BuildBatOperation()
        {
            UseExecutionBehavior(new CommandProcessBehavior(BuildCommand, UnrealCommandProcessPolicy.Instance, GetExecutionRetryPolicy));
        }

        /// <summary>
        /// Direct Build.bat-backed operations expose shared build behavior, configuration, and compiler overrides.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(BuildConfigurationOptions),
                    typeof(UbtOptions),
                    typeof(UbtCompilerOptions)
                });
        }

        protected override IEnumerable<global::LocalAutomation.Runtime.ExecutionLock> GetExecutionLocks(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            /* Direct Build.bat flows participate in the shared Unreal build lock so multiple callbacks in the same app do
               not race on UnrealBuildTool's writable rules state. */
            foreach (global::LocalAutomation.Runtime.ExecutionLock executionLock in base.GetExecutionLocks(operationParameters))
            {
                yield return executionLock;
            }

            yield return UnrealExecutionLocks.GlobalBuild;
        }

        /// <summary>
        /// Direct Build.bat invocations can rerun the complete command body after build-tool contention or a C++ compiler crash.
        /// </summary>
        private global::LocalAutomation.Runtime.ExecutionRetryPolicy? GetExecutionRetryPolicy(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            return UnrealBuildRetryPolicies.Build;
        }

        // Validate shared direct-UBT overrides once so every Build.bat-backed operation enforces the same limits.
        protected override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            string? engineSelectionError = GetSingleEngineSelectionValidationMessage(operationParameters);
            if (engineSelectionError != null)
            {
                return engineSelectionError;
            }

            UbtCompilerOptions buildBatOptions = operationParameters.GetOptions<UbtCompilerOptions>();

            Engine? engine = GetTargetEngineInstall(operationParameters);
            if (engine == null)
            {
                return null;
            }

            return UbtArguments.CheckCompilerRequirements(engine, buildBatOptions.Compiler, buildBatOptions.CppStandard);
        }

        /// <summary>Binds compiler options and the selected engine to a direct UBT command with child-only LLVM settings.</summary>
        private Command BuildCommand(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            // Let derived operations describe the target-specific portion of the Build.bat invocation first.
            Arguments args = CreateBuildArguments(operationParameters);
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            UbtCompilerOptions compilerOptions = operationParameters.GetOptions<UbtCompilerOptions>();
            UbtOptions ubtOptions = operationParameters.GetOptions<UbtOptions>();
            string? clangToolchainRoot = UbtArguments.ApplySharedBuildArguments(args, engine,
                compilerOptions.Compiler, compilerOptions.CppStandard, ubtOptions.NoHotReload);

            // Apply header regeneration to every direct target build, including plugin and deployment builds.
            if (ubtOptions.ForceHeaderGeneration)
            {
                args.SetFlag("ForceHeaderGeneration");
            }

            Command command = new(engine.GetBuildPath(), args.ToString());
            if (!string.IsNullOrWhiteSpace(clangToolchainRoot))
            {
                // UBT reads LLVM_PATH while discovering Clang, so set it only for this Build.bat process.
                command.EnvironmentVariables["LLVM_PATH"] = clangToolchainRoot;
            }

            return command;
        }

        // Derived operations provide the target-specific portion of the direct Build.bat invocation.
        protected virtual Arguments CreateBuildArguments(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            return new Arguments();
        }

        // Raw generic Build.bat children need a readable operation name even though the generic type name includes arity.
        protected override string GetOperationName()
        {
            if (GetType() == typeof(BuildBatOperation<T>))
            {
                return "Build.bat";
            }

            return base.GetOperationName();
        }

    }
}
