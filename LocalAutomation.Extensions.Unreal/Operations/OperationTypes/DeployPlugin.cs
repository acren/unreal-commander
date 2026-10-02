using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Commands;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Extensions.Unreal.Operations.BaseOperations;
using LocalAutomation.Extensions.Unreal.Operations.OperationOptionTypes;
using LocalAutomation.Extensions.Unreal.Unreal;
using Microsoft.Extensions.Logging;
using SB.FileMaterialization;
using SB.SystemUtilities.IO;
using SB.SystemUtilities.Processes;
using SB.UnrealPluginFlattening;
using SB.UnrealUtilities;
using static LocalAutomation.Runtime.LoggingExtensions;
using Package = LocalAutomation.Extensions.Unreal.Targets.Package;
using Plugin = LocalAutomation.Extensions.Unreal.Targets.Plugin;
using Project = LocalAutomation.Extensions.Unreal.Targets.Project;

namespace LocalAutomation.Extensions.Unreal.Operations.OperationTypes
{
    internal sealed class DeployPreparedSourceState
    {
        public DeployPreparedSourceState(Plugin sourcePlugin, Project hostProject)
        {
            SourcePlugin = sourcePlugin ?? throw new ArgumentNullException(nameof(sourcePlugin));
            HostProject = hostProject ?? throw new ArgumentNullException(nameof(hostProject));
        }

        public Plugin SourcePlugin { get; }

        public Project HostProject { get; }
    }

    public class DeployPluginForEngine : UnrealOperation<Plugin>
    {
        private sealed class DeploymentWorkspaceState
        {
            public DeploymentWorkspaceState(Engine engine, Plugin sourcePlugin, Project hostProject, DeploymentWorkspaceLayout layout)
            {
                Engine = engine ?? throw new ArgumentNullException(nameof(engine));
                SourcePlugin = sourcePlugin ?? throw new ArgumentNullException(nameof(sourcePlugin));
                HostProject = hostProject ?? throw new ArgumentNullException(nameof(hostProject));
                Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            }

            /// <summary>
            /// Gets the engine install used by this isolated deployment workspace.
            /// </summary>
            public Engine Engine { get; }

            /// <summary>
            /// Gets the source plugin being deployed into the per-engine workspace.
            /// </summary>
            public Plugin SourcePlugin { get; }

            /// <summary>
            /// Gets the source host project used to materialize the initial workspace project copy.
            /// </summary>
            public Project HostProject { get; }

            /// <summary>
            /// Gets every stable path namespace used by tasks in this per-engine deployment workspace.
            /// </summary>
            public DeploymentWorkspaceLayout Layout { get; }
        }

        /// <summary>
        /// Captures plugin staging decisions that later project materialization must respect.
        /// </summary>
        private sealed class DeploymentPluginStagingState
        {
            public DeploymentPluginStagingState(IReadOnlySet<string> mergePluginNames)
            {
                MergePluginNames = mergePluginNames ?? throw new ArgumentNullException(nameof(mergePluginNames));
            }

            /// <summary>
            /// Gets sibling plugins embedded into the staged plugin rather than carried as separate project plugins.
            /// </summary>
            public IReadOnlySet<string> MergePluginNames { get; }
        }

        /// <summary>
        /// Calculates stable session-output paths and persistent project-input roots for one engine-specific deployment.
        /// </summary>
        private sealed class DeploymentWorkspaceLayout
        {
            // The plugin name is reused by several path roles inside this deployment layout.
            private readonly string _pluginName;

            /// <summary>Derives stable project-input workspaces from the selected engine and project model identity.</summary>
            public DeploymentWorkspaceLayout(
                Engine engine,
                string pluginName,
                global::LocalAutomation.Runtime.Workspace sessionWorkspace,
                Project workspaceProject)
            {
                Engine resolvedEngine = engine ?? throw new ArgumentNullException(nameof(engine));
                _ = workspaceProject ?? throw new ArgumentNullException(nameof(workspaceProject));
                _pluginName = string.IsNullOrWhiteSpace(pluginName)
                    ? throw new ArgumentException("Plugin name is required for deployment workspace paths.", nameof(pluginName))
                    : pluginName;
                InstalledEnginePluginPath = EnginePathUtils.GetMarketplacePluginPath(resolvedEngine, _pluginName);
                Workspace = sessionWorkspace ?? throw new ArgumentNullException(nameof(sessionWorkspace));

                // Persistent namespaced workspaces are actual project/plugin input roots, so their identity is derived here with the layout.
                ExampleProjectBaseWorkspace = global::LocalAutomation.Runtime.Workspaces.Persistent(UnrealWorkspaceKeys.ProjectWorkspace(resolvedEngine, workspaceProject.Model, workspaceNamespace: "ExampleProjectBase"));
                ClangVariantWorkspace = global::LocalAutomation.Runtime.Workspaces.Persistent(UnrealWorkspaceKeys.ProjectWorkspace(resolvedEngine, workspaceProject.Model, workspaceNamespace: "ClangValidationVariant"));
                EnginePluginVariantWorkspace = global::LocalAutomation.Runtime.Workspaces.Persistent(UnrealWorkspaceKeys.ProjectWorkspace(resolvedEngine, workspaceProject.Model, workspaceNamespace: "EnginePluginVariant"));
                BlueprintDemoVariantWorkspace = global::LocalAutomation.Runtime.Workspaces.Persistent(UnrealWorkspaceKeys.ProjectWorkspace(resolvedEngine, workspaceProject.Model, workspaceNamespace: "BlueprintDemoVariant"));
                DistributablePluginPackageWorkspace = global::LocalAutomation.Runtime.Workspaces.Persistent(UnrealWorkspaceKeys.PluginPackage(resolvedEngine, "DistributablePluginPackage", _pluginName));
            }

            /// <summary>
            /// Gets the session-scoped workspace that holds run-specific artifacts and disposable source copies.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace Workspace { get; }

            /// <summary>
            /// Gets the persistent project root used for the project-plugin build and package branch.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace ExampleProjectBaseWorkspace { get; }

            /// <summary>
            /// Gets the persistent project root used for the optional Clang validation branch.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace ClangVariantWorkspace { get; }

            /// <summary>
            /// Gets the persistent project root used for the engine-plugin package branch.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace EnginePluginVariantWorkspace { get; }

            /// <summary>
            /// Gets the persistent project root shared by blueprint-only and shipping demo branches.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace BlueprintDemoVariantWorkspace { get; }

            /// <summary>
            /// Gets the persistent host project root used as the UAT BuildPlugin input for distributable plugin packaging.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace DistributablePluginPackageWorkspace { get; }

            public string RootPath => Workspace.RootPath;

            // Session paths are deleted per run; persistent project roots are separate first-class build inputs.
            public string WorkspaceProjectPath => PathFor("HostProject");
            public string WorkspacePluginPath => Path.Combine(WorkspaceProjectPath, "Plugins", _pluginName);
            public string StagingPluginPath => PathFor("PluginStaging", _pluginName);
            public string BuiltPluginPath => PathFor("PluginBuild", _pluginName);
            public string ExampleProjectBasePath => ExampleProjectBaseWorkspace.RootPath;
            public string EnginePluginVariantPath => EnginePluginVariantWorkspace.RootPath;
            public string ClangVariantPath => ClangVariantWorkspace.RootPath;
            public string BlueprintDemoVariantPath => BlueprintDemoVariantWorkspace.RootPath;
            /// <summary>
            /// Gets the run-scoped empty project root used to validate engine-installed plugin discovery without project plugin files.
            /// </summary>
            public string EmptyEnginePluginProjectPath => PathFor("EmptyEnginePluginProject");
            public string ProjectPluginOperationOutputPath => PathFor("ProjectPluginPackage");
            public string EnginePluginOperationOutputPath => PathFor("EnginePluginPackage");
            public string BlueprintOperationOutputPath => PathFor("BlueprintOnlyPackage");
            public string DemoOperationOutputPath => PathFor("DemoExe");
            /// <summary>
            /// Gets the run-scoped output root used only for strict include validation package passes.
            /// </summary>
            public string StrictIncludeValidationOutputPath => PathFor("StrictIncludeValidationOutput");
            public string BlueprintTestPackageSnapshotPath => PathFor("BlueprintPackageTestSnapshot");
            public string ExampleArchiveProjectPath => PathFor("ExampleProjectArchive");
            public string InstalledEnginePluginPath { get; }
            public string PrebuildProjectPluginBaseOutputPath => PathFor("PrebuildProjectPluginBaseOutput");
            public string ClangCheckOutputPath => PathFor("ClangCheckOutput");
            public string ProjectPluginBaseEditorLaunchOutputPath => PathFor("ProjectPluginBaseEditorLaunchOutput");
            public string ProjectPluginBaseStandaloneLaunchOutputPath => PathFor("ProjectPluginBaseStandaloneLaunchOutput");
            public string ProjectPluginBaseQueryTargetsOutputPath => PathFor("ProjectPluginBaseQueryTargetsOutput");
            public string ProjectPluginLaunchOutputPath => PathFor("ProjectPluginLaunchOutput");
            public string EnginePluginLaunchOutputPath => PathFor("EnginePluginLaunchOutput");
            /// <summary>
            /// Gets the run-scoped launch output root for the empty project that resolves the plugin from the engine install.
            /// </summary>
            public string EmptyEnginePluginProjectEditorLaunchOutputPath => PathFor("EmptyEnginePluginProjectEditorLaunchOutput");
            public string BlueprintLaunchOutputPath => PathFor("BlueprintLaunchOutput");

            private string PathFor(string label)
            {
                return Workspace.GetPath(global::LocalAutomation.Runtime.ExecutionPathConventions.MakeCompactSegment(label));
            }

            private string PathFor(string label, string leafName)
            {
                return Path.Combine(PathFor(label), leafName);
            }
        }

        /// <summary>
        /// Gets the isolated per-engine temp root so multiple engine-specific execution scopes can run without colliding
        /// in shared staging or package folders.
        /// </summary>
        private string GetEngineTempPath(global::LocalAutomation.Runtime.ExecutionTaskContext context, Engine engine)
        {
            return Path.Combine(base.GetOperationTempPath(context), $"UE_{engine.Version.MajorMinorString}");
        }

        /// <summary>
        /// Creates one validated plugin target from a known workspace-relative plugin path.
        /// </summary>
        private static Plugin CreateRequiredPlugin(string pluginPath, string failureMessage)
        {
            if (!PluginPaths.Instance.IsTargetDirectory(pluginPath))
            {
                throw new InvalidOperationException($"{failureMessage}: {pluginPath}");
            }

            return new Plugin(pluginPath);
        }

        /// <summary>
        /// Creates one validated project target from a known workspace-relative project path.
        /// </summary>
        private static Project CreateRequiredProject(string projectPath, string failureMessage)
        {
            if (!ProjectPaths.Instance.IsTargetDirectory(projectPath))
            {
                throw new InvalidOperationException($"{failureMessage}: {projectPath}");
            }

            return new Project(projectPath);
        }

        /// <summary>
        /// Creates one validated packaged-build target from one session-scoped prepared-project package output.
        /// </summary>
        private static Package CreateRequiredSessionPackage(string outputPath, DeploymentWorkspaceState state, string failureMessage)
        {
            return new Package(ProjectPackaging.GetRequiredPackagePath(outputPath, state.Engine, failureMessage));
        }

        /// <summary>
        /// Creates one validated packaged-build target from a known package directory path.
        /// </summary>
        private static Package CreateRequiredPackage(string packagePath, string failureMessage)
        {
            if (!PackagePaths.Instance.IsTargetDirectory(packagePath))
            {
                throw new InvalidOperationException($"{failureMessage}: {packagePath}");
            }

            return new Package(packagePath);
        }

        /// <summary>
        /// Returns one archive zip path beneath the operation output archive folder.
        /// </summary>
        private string GetArchiveZipPath(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, string archivePrefix, string archiveFileName)
        {
            return Path.Combine(GetOutputPath(operationParameters), "Archives", archivePrefix + archiveFileName);
        }

        /// <summary>
        /// Describes the per-engine deployment subtree beneath the framework-owned root task.
        /// </summary>
        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            Engine engine = GetTargetEngineInstall(operationParameters)
                ?? throw new InvalidOperationException("Deploy Plugin For Engine requires a resolved Unreal engine during plan authoring.");
            AutomationOptions automationOptions = operationParameters.GetOptions<AutomationOptions>();
            PluginDeployOptions deployOptions = operationParameters.GetOptions<PluginDeployOptions>();

            /* The per-engine flow is authored as an explicit DAG so validation, packaging, and variant-preparation work can
               widen where the filesystem inputs are independent. Visible task dependencies still wait for each authored
               subtree to finish because joins target the visible task ids rather than hidden body-task ids. */
            root.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, steps =>
            {
                /* Workspace preparation stands on its own because later project-variant materialization only needs the
                   workspace copy, while plugin packaging owns every plugin-specific staging, UAT package, and archive step
                   that fans out from that workspace. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareWorkspace = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder stagePlugin = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder pluginArtifactsFlow = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder strictIncludeValidation = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packagePluginArtifact = default!;
                PluginBuildOptions pluginBuildOptions = operationParameters.GetOptions<PluginBuildOptions>();
                prepareWorkspace = steps.Task("Prepare Workspace")
                    .Describe("Create the isolated engine-specific workspace from the prepared source")
                    .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                    .Run(PrepareStepAsync);

                pluginArtifactsFlow = steps.Task("Plugin Packaging")
                    .Describe("Stage, package, and archive the plugin artifacts used by later deploy branches")
                    .After(prepareWorkspace.Id);
                pluginArtifactsFlow.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, pluginArtifactScope =>
                {
                    stagePlugin = pluginArtifactScope.Task("Stage Plugin")
                        .Describe("Create the staged plugin copy and persistent UAT BuildPlugin package input used for packaging and archiving")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.DistributablePluginPackageWorkspace.MutationLocks)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(StagingStepAsync);

                    global::LocalAutomation.Runtime.ExecutionTaskBuilder removeExistingEnginePluginInstall = pluginArtifactScope.Task("Remove Existing Engine Plugin Install")
                        .Describe("Delete stale engine-installed plugin files before UBT scans plugin descriptors for distributable packaging")
                        .WithExecutionLocks(UnrealExecutionLocks.GlobalBuild)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(RemoveExistingEnginePluginInstallAsync);

                    strictIncludeValidation = pluginArtifactScope.AddChildOperation(
                            "Validate Strict Includes",
                            new PackagePlugin(),
                            () => CreatePluginPackageAuthoringParameters(operationParameters, strictIncludes: true),
                            "Run UAT BuildPlugin strict include validation against the staged package input without feeding distributable archives",
                            context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                global::LocalAutomation.Runtime.Workspace workspace = state.Layout.DistributablePluginPackageWorkspace;
                                Plugin packagePlugin = CreateRequiredPlugin(workspace.GetPath("HostProject", "Plugins", state.SourcePlugin.Name), "Persistent host-project plugin is not available for strict include validation");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = packagePlugin;
                                parameters.OutputPathOverride = state.Layout.StrictIncludeValidationOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };

                                // Strict include validation is intentionally isolated from the distributable package output.
                                parameters.GetOptions<PluginBuildOptions>().StrictIncludes = true;
                                return parameters;
                            })
                        .When(pluginBuildOptions.StrictIncludes, "Strict Includes is off.")
                        .After(stagePlugin.Id, removeExistingEnginePluginInstall.Id)
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.DistributablePluginPackageWorkspace.MutationLocks
                            .Append(UnrealExecutionLocks.GetAutomationToolLock(context.GetData<DeploymentWorkspaceState>().Engine))
                            .Append(UnrealExecutionLocks.GlobalBuild));

                    packagePluginArtifact = pluginArtifactScope.AddChildOperation(
                            new PackagePlugin(),
                            () => CreatePluginPackageAuthoringParameters(operationParameters, strictIncludes: false),
                            context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                global::LocalAutomation.Runtime.Workspace workspace = state.Layout.DistributablePluginPackageWorkspace;
                                Plugin packagePlugin = CreateRequiredPlugin(workspace.GetPath("HostProject", "Plugins", state.SourcePlugin.Name), "Persistent host-project plugin is not available for packaging");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = packagePlugin;
                                parameters.OutputPathOverride = state.Layout.BuiltPluginPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };

                                // Distributable package artifacts use normal BuildPlugin outputs so validation flags do not inflate archives.
                                parameters.GetOptions<PluginBuildOptions>().StrictIncludes = false;
                                return parameters;
                            })
                        .After(stagePlugin.Id, removeExistingEnginePluginInstall.Id)
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.DistributablePluginPackageWorkspace.MutationLocks
                            .Append(UnrealExecutionLocks.GetAutomationToolLock(context.GetData<DeploymentWorkspaceState>().Engine))
                            .Append(UnrealExecutionLocks.GlobalBuild));

                    pluginArtifactScope.Task("Archive Staged Plugin Source")
                        .Describe("Archive the staged source-style plugin payload as soon as the staging copy is ready")
                        .After(stagePlugin.Id)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(ArchivePluginSourceAsync);

                    pluginArtifactScope.Task("Archive Distributable Plugin")
                        .Describe("Archive the packaged distributable plugin payload as soon as the built plugin output is ready")
                        .After(packagePluginArtifact.Id)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(ArchivePluginBuildAsync);
                });

                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareSharedBase = steps.Task("Prepare Shared Project-Plugin Base")
                    .Describe("Materialize, populate, and prebuild the shared code example base that later package branches clone or package directly")
                    .After(prepareWorkspace.Id, stagePlugin.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder materializeProjectPluginBase = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder installProjectPluginBase = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildExampleBase = default!;
                prepareSharedBase.Children(sharedBaseScope =>
                        {
                            materializeProjectPluginBase = sharedBaseScope.Task("Materialize Project-Plugin Base")
                                .Describe("Copy the shared code example base from the workspace project before the built plugin is installed into it")
                                .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
                                .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                                .Run(MaterializeProjectPluginBaseAsync);

                            installProjectPluginBase = sharedBaseScope.Task("Install Distributable Plugin Into Project-Plugin Base")
                                    .Describe("Copy the built distributable plugin into the shared project-plugin base before downstream package variants clone it")
                                    .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
                                    .After(materializeProjectPluginBase.Id, packagePluginArtifact.Id)
                                    .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                                    .Run(InstallDistributablePluginIntoProjectPluginBaseAsync);

                            buildExampleBase = sharedBaseScope.AddChildOperation(
                                    "Prebuild Project-Plugin Base",
                                    new BuildEditorTarget(),
                                    () => CreateProjectBuildAuthoringParameters(operationParameters, BuildConfiguration.Development),
                                    "Build the shared code example editor target that later package branches can reuse",
                                    context =>
                                    {
                                        DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                        Project project = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for editor target prebuild");
                                        global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                        parameters.Target = project;
                                        parameters.OutputPathOverride = state.Layout.PrebuildProjectPluginBaseOutputPath;
                                        parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                        parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
                                        EnableDeployUbtOptions(parameters);
                                        return parameters;
                                    })
                                .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks);
                        });

                /* The commandlet and launch validation children all fan out from the shared prebuilt base, so the common
                   dependency belongs on the validation parent group instead of being repeated on each child task. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder validateSharedBase = steps.Task("Validate Shared Project-Plugin Base")
                    .Describe("Run optional commandlet and launch validation against the shared prebuilt project-plugin base while later packaging preparation continues")
                    .After(prepareSharedBase.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder testEditor = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder testStandalone = default!;
                validateSharedBase.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, validationScope =>
                {
                    validationScope.AddChildOperation(
                            "Run Data Validation",
                            new ValidateProjectData(),
                            () => CreateDataValidationParameters(plugin.HostProject, engine),
                            "Run Unreal data validation against the prepared distributable project-plugin base",
                            context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                Project project = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for data validation");
                                return CreateDataValidationParameters(project, state.Engine);
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
                        .When(deployOptions.RunDataValidation, "Run Data Validation is off.");

                    global::LocalAutomation.Runtime.ExecutionTaskBuilder queryTargets = validationScope.Task("Query Project-Plugin Base Targets")
                        .Describe("Generate Unreal target metadata before the editor validation launch so editor startup can reuse the target cache")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks
                            .Append(UnrealExecutionLocks.GlobalBuild))
                        .When(automationOptions.RunTests, "Run Tests is off.")
                        .Run(QueryProjectPluginBaseTargetsAsync);

                    testEditor = validationScope.Task("Test Project-Plugin Base Editor")
                        .Describe("Launch and validate the prebuilt project-plugin base in the editor after target metadata is ready")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
                        .After(queryTargets.Id)
                        .When(automationOptions.RunTests, "Run Tests is off.")
                        .Run(context => LaunchProjectPluginBaseEditorAsync(context, automationOptions));

                    testStandalone = validationScope.Task("Test Project-Plugin Base Standalone")
                        .Describe("Launch and validate the prebuilt project-plugin base in standalone mode before downstream packaging completes")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
                        .When(automationOptions.RunTests && deployOptions.TestStandalone, automationOptions.RunTests ? "Test Standalone is off." : "Run Tests is off.")
                        .Run(context => TestProjectPluginBaseStandaloneAsync(context, automationOptions));
                });

                /* The Clang branch is its own optional sequential flow: first clone the shared base, then rebuild the
                   packaged plugin under Clang validation rules. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder clangValidationFlow = steps.Task("Clang Validation")
                    .Describe("Prepare the Clang validation variant and rebuild the distributable plugin payload under Clang")
                    .After(prepareSharedBase.Id)
                    .When(deployOptions.RunClangCompileCheck, "Run Clang Compile Check is off.");
                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareClangVariant = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder clangCheck = default!;
                clangValidationFlow.Children(clangScope =>
                {
                    prepareClangVariant = clangScope.Task("Prepare Clang Validation Variant")
                        .Describe("Clone the prebuilt project-plugin base for the optional Clang validation branch")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ClangVariantWorkspace.MutationLocks)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(PrepareClangVariantAsync);

                    clangCheck = clangScope.AddChildOperation(
                        "Run Clang Validation",
                        new BuildPlugin(),
                        () => CreatePluginBuildAuthoringParameters(operationParameters, UbtCompiler.Clang),
                        "Rebuild the packaged plugin in the Clang validation variant to verify the distributable plugin payload under Clang",
                        context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                using Project clangProject = CreateRequiredProject(state.Layout.ClangVariantPath, "Clang validation project is not available for plugin build");
                                Plugin clangPlugin = CreateRequiredPlugin(Path.Combine(clangProject.Model.PluginsPath, state.SourcePlugin.Name), "Clang validation plugin is not available for build");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = clangPlugin;
                                parameters.OutputPathOverride = state.Layout.ClangCheckOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
                                UbtCompilerOptions compilerOptions = parameters.GetOptions<UbtCompilerOptions>();
                                compilerOptions.Compiler = UbtCompiler.Clang;
                                compilerOptions.CppStandard = UbtCppStandard.Default;
                                EnableDeployUbtOptions(parameters);
                                return parameters;
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ClangVariantWorkspace.MutationLocks);
                });

                /* These variant clones are independent siblings with the same prerequisite, so the shared dependency is
                   authored once on the parent group and inherited by each sequentially independent child declaration. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder preparePackageVariants = steps.Task("Prepare Package Variants")
                    .Describe("Clone and mutate the shared prebuilt project-plugin base into the engine and blueprint packaging variants before the package flows begin")
                    .After(prepareSharedBase.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareEngineVariant = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareBlueprintDemoVariant = default!;
                preparePackageVariants.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, variantScope =>
                {
                    prepareEngineVariant = variantScope.Task("Prepare Engine-Plugin Variant")
                        .Describe("Clone the prebuilt project-plugin base and remove the project-level plugin so packaging resolves the built plugin from the engine install")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.EnginePluginVariantWorkspace.MutationLocks)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(PrepareEnginePluginVariantAsync);

                    prepareBlueprintDemoVariant = variantScope.Task("Prepare Blueprint And Demo Variant")
                        .Describe("Clone the prebuilt project-plugin base, remove the project-level plugin, convert to blueprint-only, and prune plugins for blueprint and demo packaging")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(PrepareBlueprintDemoVariantAsync);
                });

                /* The project-plugin packaging branch reuses the shared prebuilt base directly, but only once every clone
                   branch has finished copying that base. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageProjectPluginFlow = steps.Task("Project-Plugin Package")
                    .Describe("Build, package, and validate the example that keeps the built plugin installed at project level")
                    .After(prepareClangVariant.Id, preparePackageVariants.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildProjectPluginPackageTarget = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageProjectPlugin = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder testProjectPlugin = default!;
                packageProjectPluginFlow.Children(flowScope =>
                {
                    buildProjectPluginPackageTarget = flowScope.AddChildOperation(
                        "Build Project-Plugin Package Target",
                        new BuildProjectTarget(),
                        () => CreateProjectBuildAuthoringParameters(operationParameters, BuildConfiguration.Development, "-nocompileeditor"),
                        "Build the shared project-plugin example target after all package variants have finished cloning the shared base",
                        context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                Project project = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for package target build");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = project;
                                parameters.OutputPathOverride = state.Layout.ProjectPluginOperationOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
                                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
                                EnableDeployUbtOptions(parameters);
                                return parameters;
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks);

                    packageProjectPlugin = flowScope.Task("Package Project-Plugin Example")
                        .Describe("Package the shared prebuilt code example with the built plugin still installed at project level after variant cloning finishes")
                        .WithExecutionLocks(context =>
                        {
                            DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                            return state.Layout.ExampleProjectBaseWorkspace.MutationLocks
                                .Append(UnrealExecutionLocks.GetAutomationToolLock(state.Engine));
                        })
                        .After(buildProjectPluginPackageTarget.Id)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(PackageProjectPluginExampleAsync);

                    testProjectPlugin = flowScope.Task("Test Project-Plugin Example")
                        .Describe("Launch and validate the packaged code example that keeps the built plugin installed at project level")
                        .After(packageProjectPlugin.Id)
                        .When(automationOptions.RunTests && deployOptions.TestPackageWithProjectPlugin, automationOptions.RunTests ? "Test Package With Project Plugin is off." : "Run Tests is off.")
                        .Run(context => TestProjectPluginExampleAsync(context, automationOptions));
                });

                /* Installing the built plugin into the engine is a shared handoff step for later engine-resolving
                   branches, not part of the project-plugin validation flow itself. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder installEnginePlugin = steps.Task("Install Built Plugin To Engine")
                    .Describe("Install the built plugin into the engine marketplace folder once the project-plugin example package is sealed")
                    .After(packageProjectPlugin.Id)
                    .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                    .Run(InstallBuiltPluginToEngineAsync);

                /* This lightweight launch validates the engine-installed plugin in a generated project with no project
                   plugin files, so Unreal must discover the plugin from the engine marketplace install. */
                steps.Task("Test Empty Engine-Plugin Project")
                    .Describe("Launch and validate a generated empty project that enables the built plugin only from the engine install")
                    .After(installEnginePlugin.Id)
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                    .Run(context => TestEmptyEnginePluginProjectAsync(context, automationOptions));

                global::LocalAutomation.Runtime.ExecutionTaskBuilder enginePluginFlow = steps.Task("Engine-Plugin Package")
                    .Describe("Build, package, and validate the example that loads the built plugin from the engine install after its prepared variant is ready")
                    .After(prepareEngineVariant.Id, installEnginePlugin.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildEnginePluginPackageTarget = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageEnginePlugin = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder testEnginePlugin = default!;
                enginePluginFlow.Children(flowScope =>
                {
                    buildEnginePluginPackageTarget = flowScope.AddChildOperation(
                        "Build Engine-Plugin Package Target",
                        new BuildProjectTarget(),
                        () => CreateProjectBuildAuthoringParameters(operationParameters, BuildConfiguration.Development, "-nocompileeditor"),
                        "Build the engine-plugin example target after the engine variant is prepared and the built plugin is installed into the engine",
                        context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                Project project = CreateRequiredProject(state.Layout.EnginePluginVariantPath, "Engine-plugin variant is not available for package target build");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = project;
                                parameters.OutputPathOverride = state.Layout.EnginePluginOperationOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
                                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
                                EnableDeployUbtOptions(parameters);
                                return parameters;
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.EnginePluginVariantWorkspace.MutationLocks);

                    packageEnginePlugin = flowScope.Task("Package Engine-Plugin Example")
                        .Describe("Package the code example variant that resolves the built plugin from the engine install")
                        .WithExecutionLocks(context =>
                        {
                            DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                            return state.Layout.EnginePluginVariantWorkspace.MutationLocks
                                .Append(UnrealExecutionLocks.GetAutomationToolLock(state.Engine));
                        })
                        .After(buildEnginePluginPackageTarget.Id)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(PackageEnginePluginExampleAsync);

                    testEnginePlugin = flowScope.Task("Test Engine-Plugin Example")
                        .Describe("Launch and validate the packaged code example that resolves the built plugin from the engine install")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.EnginePluginVariantWorkspace.MutationLocks)
                        .After(packageEnginePlugin.Id)
                        .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                        .Run(context => TestEnginePluginExampleAsync(context, automationOptions));
                });

                /* The blueprint package is the shared prerequisite for launch validation and for demo packaging. The test
                   branch launches from a copied package snapshot so the later shipping package pass can safely recreate the
                   staged output in parallel. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder blueprintDemoFlow = steps.Task("Blueprint And Demo")
                    .Describe("Build, package, validate, and archive the blueprint and shipping demo outputs after the prepared variant is ready")
                    .After(prepareBlueprintDemoVariant.Id, installEnginePlugin.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildBlueprintPackageTarget = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageBlueprint = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder copyBlueprintPackageForTest = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder testBlueprint = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder demoPackageFlow = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildDemoTarget = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageDemo = default!;
                blueprintDemoFlow.Children(flowScope =>
                {
                    buildBlueprintPackageTarget = flowScope.AddChildOperation(
                        "Build Blueprint-Only Package Target",
                        new BuildProjectTarget(),
                        () => CreateProjectBuildAuthoringParameters(operationParameters, BuildConfiguration.Development, "-nocompileeditor"),
                        "Build the blueprint-only example target after the blueprint/demo variant is prepared and the built plugin is installed into the engine",
                        context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                Project project = CreateRequiredProject(state.Layout.BlueprintDemoVariantPath, "Blueprint/demo variant is not available for package target build");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = project;
                                parameters.OutputPathOverride = state.Layout.BlueprintOperationOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
                                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
                                EnableDeployUbtOptions(parameters);
                                return parameters;
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks);

                    packageBlueprint = flowScope.Task("Package Blueprint-Only Example")
                        .Describe("Package the blueprint-only example variant that resolves the built plugin from the engine install")
                        .WithExecutionLocks(context =>
                        {
                            DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                            return state.Layout.BlueprintDemoVariantWorkspace.MutationLocks
                                .Append(UnrealExecutionLocks.GetAutomationToolLock(state.Engine));
                        })
                        .After(buildBlueprintPackageTarget.Id)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(PackageBlueprintOnlyExampleAsync);
                });

                /* The package task produces the shared staged output, so later blueprint launch validation and demo
                   packaging branches depend on that completed package step instead of being authored as static children
                   under the runnable package task itself. */
                copyBlueprintPackageForTest = steps.Task("Copy Blueprint Package For Test")
                    .Describe("Copy the packaged blueprint-only build to a dedicated snapshot so launch validation stays stable while demo packaging recreates the shared staged output")
                    .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks)
                    .After(packageBlueprint.Id)
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                    .Run(CopyBlueprintPackageForTestAsync);

                testBlueprint = steps.Task("Test Blueprint-Only Example")
                    .Describe("Launch and validate the packaged blueprint-only example that resolves the built plugin from the engine install")
                    .After(copyBlueprintPackageForTest.Id)
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .Run(context => TestBlueprintOnlyExampleAsync(context, automationOptions));

                demoPackageFlow = steps.Task("Demo Package")
                    .Describe("Build and package the shipping demo executable from the prepared blueprint/demo variant once the development blueprint package has been snapshotted for launch validation")
                    .After(packageBlueprint.Id);
                demoPackageFlow.Children(demoFlowScope =>
                {
                    buildDemoTarget = demoFlowScope.AddChildOperation(
                        "Build Demo Executable Target",
                        new BuildProjectTarget(),
                        () => CreateProjectBuildAuthoringParameters(operationParameters, BuildConfiguration.Shipping, "-nocompileeditor"),
                        "Build the shipping demo target from the prepared blueprint/demo variant in parallel with blueprint launch validation",
                        context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                Project project = CreateRequiredProject(state.Layout.BlueprintDemoVariantPath, "Blueprint/demo variant is not available for demo target build");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = project;
                                parameters.OutputPathOverride = state.Layout.DemoOperationOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Shipping;
                                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
                                EnableDeployUbtOptions(parameters);
                                return parameters;
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks);

                    packageDemo = demoFlowScope.Task("Package Demo Executable")
                        .Describe("Package the shipping demo executable from the prepared blueprint/demo variant after the demo target build and blueprint test snapshot are ready")
                        .WithExecutionLocks(context =>
                        {
                            DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                            return state.Layout.BlueprintDemoVariantWorkspace.MutationLocks
                                .Append(UnrealExecutionLocks.GetAutomationToolLock(state.Engine));
                        })
                        .After(copyBlueprintPackageForTest.Id)
                        .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                        .Run(PackageDemoExecutableAsync);
                });

                steps.Task("Archive Example Project Payload")
                    .Describe("Archive the example-project payload from a dedicated archive copy once the blueprint and demo variant is prepared")
                    .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks)
                    .After(prepareBlueprintDemoVariant.Id)
                    .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                    .Run(ArchiveExampleProjectAsync);

                steps.Task("Archive Demo Executable")
                    .Describe("Archive the packaged demo executable as soon as the demo output exists")
                    .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks)
                    .After(packageDemo.Id)
                    .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                    .Run(ArchiveDemoPackageAsync);

            });
        }

        /// <summary>
        /// Per-engine deployment reuses the same option groups as the outer deployment flow because it reads the shared
        /// deployment settings directly while orchestrating child operations.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(AutomationOptions),
                    typeof(PluginBuildOptions),
                    typeof(PluginDeployOptions)
                });
        }

        /// <summary>
        /// Runs the scheduler-backed prepare step.
        /// </summary>
        private async Task PrepareStepAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing engine workspace");
            global::LocalAutomation.Runtime.ValidatedOperationParameters validatedParameters = context.ValidatedOperationParameters;
            Engine engine = GetTargetEngineInstall(validatedParameters)
                ?? throw new Exception("Engine not specified");
            DeployPreparedSourceState preparedSource = context.GetData<DeployPreparedSourceState>();
            Plugin plugin = preparedSource.SourcePlugin;
            Project hostProject = preparedSource.HostProject;
            global::LocalAutomation.Runtime.Workspace sessionWorkspace = global::LocalAutomation.Runtime.Workspaces.Session(GetEngineTempPath(context, engine));
            string sessionRootPath = sessionWorkspace.RootPath;
            string workspaceProjectPath = sessionWorkspace.GetPath(global::LocalAutomation.Runtime.ExecutionPathConventions.MakeCompactSegment("HostProject"));

            context.Logger.LogInformation($"Engine version: {engine.Version}");
            context.Logger.LogInformation($"Source host project: {hostProject.Model.ProjectPath}");
            context.Logger.LogInformation($"Source plugin: {plugin.Model.PluginPath}");
            context.Logger.LogInformation($"Session workspace root: {sessionRootPath}");

            context.Logger.LogInformation($"Deleting existing session workspace root: {sessionRootPath}");
            FileUtils.DeleteDirectoryIfExists(sessionRootPath);
            context.Logger.LogInformation($"Creating session workspace root: {sessionRootPath}");
            Directory.CreateDirectory(sessionRootPath);

            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            using Project workspaceProject = new(PluginDeployment.PrepareWorkspace(plugin.Model, hostProject.Model, engine,
                workspaceProjectPath, deployOptions.IncludeOtherPlugins, deployOptions.ExcludePlugins, context.Logger, context.CancellationToken));

            DeploymentWorkspaceLayout layout = new(engine, plugin.Name, sessionWorkspace, workspaceProject);
            DeploymentWorkspaceState workspaceState = new(engine, plugin, hostProject, layout);
            string archivePrefix = await BuildArchivePrefixAsync(workspaceState);
            context.Logger.LogInformation($"Archive name prefix is '{archivePrefix}'");
            context.Logger.LogInformation($"Project-plugin base root: {layout.ExampleProjectBasePath}");
            context.Logger.LogInformation($"Clang validation root: {layout.ClangVariantPath}");
            context.Logger.LogInformation($"Engine-plugin variant root: {layout.EnginePluginVariantPath}");
            context.Logger.LogInformation($"Blueprint/demo variant root: {layout.BlueprintDemoVariantPath}");

            context.SetOperationData(workspaceState);
            context.Logger.LogInformation("Stored workspace state for later deployment branches");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Runs the scheduler-backed staging step.
        /// </summary>
        private async Task StagingStepAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing plugin staging copy");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string workspacePluginPath = state.Layout.WorkspacePluginPath;
            string stagingPluginPath = state.Layout.StagingPluginPath;
            context.Logger.LogInformation($"Engine version: {state.Engine.Version}");
            context.Logger.LogInformation($"Workspace plugin: {workspacePluginPath}");
            context.Logger.LogInformation($"Staging destination: {stagingPluginPath}");
            using Plugin workspacePlugin = CreateRequiredPlugin(workspacePluginPath, "Workspace plugin is not available for staging");
            state.Layout.DistributablePluginPackageWorkspace.EnsureReady(context.Logger);
            string packageInputPluginPath = state.Layout.DistributablePluginPackageWorkspace.GetPath("HostProject", "Plugins", workspacePlugin.Name);
            IReadOnlyList<MergePlugin> mergePlugins = ResolveMergePlugins(
                state.HostProject.Model,
                context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>().MergePlugins);
            IReadOnlySet<string> mergePluginNames = PluginDeployment.StagePlugin(
                state.SourcePlugin.Model,
                workspacePlugin.Model,
                state.Engine,
                stagingPluginPath,
                packageInputPluginPath,
                mergePlugins,
                context.Logger,
                context.CancellationToken);
            context.SetOperationData(new DeploymentPluginStagingState(mergePluginNames));
            await Task.CompletedTask;
        }

        /// <summary>
        /// Converts the target-local editing syntax into the flattener's explicit descriptor inputs before deployment begins.
        /// </summary>
        private static IReadOnlyList<MergePlugin> ResolveMergePlugins(SB.UnrealUtilities.Project hostProject,
            string mergePluginsText)
        {
            if (string.IsNullOrWhiteSpace(mergePluginsText))
            {
                return Array.Empty<MergePlugin>();
            }

            // The persisted value is presentation syntax; the reusable deployment step receives only source identities.
            List<MergePlugin> mergePlugins = new();
            foreach (string rawEntry in mergePluginsText.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = rawEntry.Split(new[] { '=' }, 2, StringSplitOptions.TrimEntries);
                if (string.IsNullOrWhiteSpace(parts[0]))
                {
                    continue;
                }

                string descriptorPath = ResolveMergePluginDescriptorPath(hostProject, parts[0]);
                string? embeddedPrefix = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
                mergePlugins.Add(new MergePlugin(descriptorPath, embeddedPrefix));
            }

            return mergePlugins;
        }

        /// <summary>
        /// Resolves one UI-entered plugin path or name without letting host option syntax leak into the deployment capability.
        /// </summary>
        private static string ResolveMergePluginDescriptorPath(SB.UnrealUtilities.Project hostProject, string sourcePluginSpecifier)
        {
            foreach (string candidatePath in GetExplicitMergePluginPathCandidates(hostProject, sourcePluginSpecifier))
            {
                if (PluginPaths.Instance.IsTargetDirectory(candidatePath))
                {
                    return new SB.UnrealUtilities.Plugin(Path.GetFullPath(candidatePath)).UPluginPath;
                }
            }

            if (!Directory.Exists(hostProject.PluginsPath))
            {
                throw new DirectoryNotFoundException($"Could not resolve merge plugin '{sourcePluginSpecifier}' because '{hostProject.PluginsPath}' does not exist.");
            }

            string pluginName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePluginSpecifier));
            string[] matches = Directory.GetDirectories(hostProject.PluginsPath, "*", SearchOption.AllDirectories)
                .Where(PluginPaths.Instance.IsTargetDirectory)
                .Where(path => string.Equals(Path.GetFileName(path), pluginName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return matches.Length switch
            {
                0 => throw new DirectoryNotFoundException($"Could not resolve merge plugin '{sourcePluginSpecifier}' under '{hostProject.PluginsPath}'."),
                1 => new SB.UnrealUtilities.Plugin(Path.GetFullPath(matches[0])).UPluginPath,
                _ => throw new InvalidOperationException($"Merge plugin '{sourcePluginSpecifier}' is ambiguous: {string.Join(", ", matches)}")
            };
        }

        /// <summary>
        /// Interprets only rooted or directory-qualified values as paths so an unqualified plugin name cannot silently
        /// select a top-level sibling when a grouped sibling has the same name.
        /// </summary>
        private static IEnumerable<string> GetExplicitMergePluginPathCandidates(SB.UnrealUtilities.Project hostProject,
            string sourcePluginSpecifier)
        {
            if (Path.IsPathRooted(sourcePluginSpecifier))
            {
                yield return sourcePluginSpecifier;
                yield break;
            }

            if (!sourcePluginSpecifier.Contains(Path.DirectorySeparatorChar)
                && !sourcePluginSpecifier.Contains(Path.AltDirectorySeparatorChar))
            {
                yield break;
            }

            yield return Path.Combine(hostProject.ProjectPath, sourcePluginSpecifier);
            yield return Path.Combine(hostProject.PluginsPath, sourcePluginSpecifier);
        }

        /// <summary>
        /// Builds the archive filename prefix for the active engine-specific execution scope.
        /// </summary>
        private async Task<string> BuildArchivePrefixAsync(DeploymentWorkspaceState state)
        {
            string branchName = await VersionControlUtils.GetBranchNameAsync(state.HostProject.Model.ProjectPath);
            return PluginDeployment.BuildArchivePrefix(state.SourcePlugin.Model, state.Engine.Version, branchName);
        }

        /// <summary>
        /// Launches the prebuilt project-plugin base in the editor so deploy validation exercises the built plugin payload
        /// rather than the intermediate workspace copy.
        /// </summary>
        private async Task LaunchProjectPluginBaseEditorAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Launching project-plugin base editor");
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.LaunchProjectPluginBaseEditor")
                .SetTag("trigger", "StepTransition");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project projectPluginBase = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for editor test");
            global::LocalAutomation.Runtime.OperationParameters launchEditorParams = CreateParameters();
            launchEditorParams.Target = projectPluginBase;
            launchEditorParams.OutputPathOverride = state.Layout.ProjectPluginBaseEditorLaunchOutputPath;
            launchEditorParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            launchEditorParams.SetOptions(automationOptions);
            // Editor validation is an automation child process, so Unreal should treat it as a secondary process.
            ApplyValidationLaunchFlags(launchEditorParams, editorProcess: true);

            await RunChildOperationAsync<LaunchProjectEditor>(launchEditorParams, context, required: true, failureMessage: "Failed to launch project-plugin base in editor", hideChildOperationRootInGraph: true);
            activity.SetTag("result", "Completed");
        }

        /// <summary>
        /// Launches the prebuilt project-plugin base in standalone mode so deploy validation exercises the built plugin
        /// payload rather than the intermediate workspace copy.
        /// </summary>
        private async Task TestProjectPluginBaseStandaloneAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing project-plugin base in standalone");
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.TestProjectPluginBaseStandalone")
                .SetTag("trigger", "StepTransition");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project projectPluginBase = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for standalone test");
            global::LocalAutomation.Runtime.OperationParameters launchStandaloneParams = CreateParameters();
            launchStandaloneParams.Target = projectPluginBase;
            launchStandaloneParams.OutputPathOverride = state.Layout.ProjectPluginBaseStandaloneLaunchOutputPath;
            launchStandaloneParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            launchStandaloneParams.SetOptions(automationOptions);
            // Standalone validation uses the editor executable with -game, so it receives secondary-process semantics too.
            ApplyValidationLaunchFlags(launchStandaloneParams, editorProcess: true);

            await RunChildOperationAsync<LaunchStandalone>(launchStandaloneParams, context, required: true, failureMessage: "Failed to launch project-plugin base in standalone", hideChildOperationRootInGraph: true);
            activity.SetTag("result", "Completed");
        }

        /// <summary>
        /// Materializes the shared project-plugin base from the workspace project before the built plugin is overlaid.
        /// Later packaging branches clone this prepared base instead of mutating one shared project directory in place.
        /// </summary>
        private async Task MaterializeProjectPluginBaseAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Materializing project-plugin base");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string exampleProjectPath = state.Layout.ExampleProjectBasePath;

            using Project workspaceProject = CreateRequiredProject(state.Layout.WorkspaceProjectPath, "Workspace project is not available for project-plugin base materialization");
            state.Layout.ExampleProjectBaseWorkspace.EnsureReady(context.Logger);
            DeploymentPluginStagingState stagingState = context.GetOperationData<DeploymentPluginStagingState>();
            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            using Project exampleProject = new(PluginDeployment.MaterializeProjectPluginBase(state.SourcePlugin.Model,
                state.HostProject.Model, workspaceProject.Model, state.Engine, exampleProjectPath,
                deployOptions.IncludeOtherPlugins, deployOptions.ExcludePlugins, stagingState.MergePluginNames,
                context.Logger, context.CancellationToken));
            await Task.CompletedTask;
        }

        /// <summary>
        /// Installs the built distributable plugin into the already materialized project-plugin base so downstream
        /// packaging and launch validation exercise the shipped plugin payload rather than the workspace copy.
        /// </summary>
        private async Task InstallDistributablePluginIntoProjectPluginBaseAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Installing distributable plugin into project-plugin base");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project exampleProject = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for plugin installation");
            using Plugin builtPlugin = CreateRequiredPlugin(state.Layout.BuiltPluginPath, "Built plugin is not available for project-plugin base installation");
            using Plugin installedPlugin = new(PluginDeployment.InstallDistributablePluginIntoProject(
                builtPlugin.Model, exampleProject.Model, context.Logger, context.CancellationToken));
            await Task.CompletedTask;
        }

        /// <summary>
        /// Generates the project target metadata cache that Unreal Editor consumes during startup before validation launches
        /// need to start the editor process. The direct UBT query matches editor startup so launches reuse a fresh cache
        /// instead of running the query invisibly inside editor initialization.
        /// </summary>
        private async Task QueryProjectPluginBaseTargetsAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Querying project-plugin base targets");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project exampleProject = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for target query");

            // Use a fresh parameter bag so the raw UBT mode command receives only the explicit arguments required to
            // produce TargetInfo.json and does not inherit unrelated launch or package arguments from the parent flow.
            global::LocalAutomation.Runtime.OperationParameters queryTargetsParams = CreateParameters();
            queryTargetsParams.Target = exampleProject;
            queryTargetsParams.OutputPathOverride = state.Layout.ProjectPluginBaseQueryTargetsOutputPath;
            queryTargetsParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            queryTargetsParams.GetOptions<AdditionalArgumentsOptions>().Arguments = UbtArguments.CreateProjectTargetQueryArguments(exampleProject.Model).ToString();
            await RunChildOperationAsync(new BuildBatOperation<Project>(), queryTargetsParams, context, required: true, failureMessage: "Failed to query project-plugin base targets", hideChildOperationRootInGraph: true);
        }

        /// <summary>
        /// Creates one isolated Clang-validation variant from the prebuilt project-plugin base while preserving the
        /// variant workspace's own root project build-output cache.
        /// </summary>
        private async Task PrepareClangVariantAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing Clang validation variant");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string sourceProjectPath = state.Layout.ExampleProjectBasePath;
            string clangVariantPath = state.Layout.ClangVariantPath;
            using Project sourceProject = CreateRequiredProject(sourceProjectPath, "Project-plugin base is not available for Clang variant materialization");
            state.Layout.ClangVariantWorkspace.EnsureReady(context.Logger);
            using Project clangVariant = new(PluginDeployment.PrepareClangVariant(
                sourceProject.Model, clangVariantPath, context.Logger, context.CancellationToken));
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates one isolated engine-plugin packaging variant by cloning the prebuilt project-plugin base, preserving the
        /// variant workspace's own root project build-output cache, and removing the project-level plugin copy before later
        /// packaging depends on the engine install.
        /// </summary>
        private async Task PrepareEnginePluginVariantAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing engine-plugin variant");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string sourceProjectPath = state.Layout.ExampleProjectBasePath;
            string engineVariantPath = state.Layout.EnginePluginVariantPath;
            using Project sourceProject = CreateRequiredProject(sourceProjectPath, "Project-plugin base is not available for engine variant materialization");
            state.Layout.EnginePluginVariantWorkspace.EnsureReady(context.Logger);
            using Project engineVariant = new(PluginDeployment.PrepareEnginePluginVariant(
                sourceProject.Model, engineVariantPath, state.SourcePlugin.Name, context.Logger, context.CancellationToken));
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates one isolated blueprint/demo variant by cloning the prebuilt project-plugin base, preserving the variant
        /// workspace's own root project build-output cache, removing the project-level plugin copy, and converting the
        /// project to blueprint-only while preserving the selected sibling plugin set.
        /// </summary>
        private async Task PrepareBlueprintDemoVariantAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing blueprint and demo variant");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string sourceProjectPath = state.Layout.ExampleProjectBasePath;
            string blueprintVariantPath = state.Layout.BlueprintDemoVariantPath;
            using Project sourceProject = CreateRequiredProject(sourceProjectPath, "Project-plugin base is not available for blueprint/demo variant materialization");
            state.Layout.BlueprintDemoVariantWorkspace.EnsureReady(context.Logger);
            using Project blueprintVariant = new(PluginDeployment.PrepareBlueprintDemoVariant(
                sourceProject.Model, blueprintVariantPath, state.SourcePlugin.Name, context.Logger, context.CancellationToken));
            await Task.CompletedTask;
        }

        /// <summary>
        /// Binds deployment validation arguments to child options with generated flags before user text and DDC after it.
        /// </summary>
        private static void ApplyValidationLaunchFlags(global::LocalAutomation.Runtime.OperationParameters launchParameters, bool editorProcess)
        {
            Arguments validationArguments = PluginDeployment.CreateValidationLaunchArguments(editorProcess);
            // Typed flags are generated before additional arguments, allowing explicit caller text to replace their keys.
            FlagOptions flagOptions = launchParameters.GetOptions<FlagOptions>();
            flagOptions.NoMessaging = validationArguments.HasArgument(nameof(FlagOptions.NoMessaging));
            flagOptions.Multiprocess = validationArguments.HasArgument(nameof(FlagOptions.Multiprocess));

            // The deployment graph is the final DDC override; leave caller text in its original position and form.
            AdditionalArgumentsOptions additionalArguments = launchParameters.GetOptions<AdditionalArgumentsOptions>();
            additionalArguments.Arguments = string.Join(' ', new[]
            {
                additionalArguments.Arguments,
                validationArguments.GetArgument("ddc")!.ToString()
            }.Where(argument => !string.IsNullOrWhiteSpace(argument)));
        }

        /// <summary>
        /// Creates fresh data-validation parameters so deploy automation-test settings cannot add test commands.
        /// </summary>
        private global::LocalAutomation.Runtime.OperationParameters CreateDataValidationParameters(Project project, Engine engine)
        {
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateParameters();
            parameters.Target = project;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
            ApplyValidationLaunchFlags(parameters, editorProcess: true);
            return parameters;
        }

        /// <summary>
        /// Creates authoring-time package parameters from the source plugin so prepared package subtasks can be previewed.
        /// </summary>
        private global::LocalAutomation.Runtime.OperationParameters CreatePluginPackageAuthoringParameters(
            global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters,
            bool strictIncludes)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
            parameters.Target = plugin;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };

            // Child package previews must match the runtime package role instead of inheriting the deploy-wide toggle.
            parameters.GetOptions<PluginBuildOptions>().StrictIncludes = strictIncludes;
            return parameters;
        }

        /// <summary>
        /// Creates authoring-time project-build parameters from the source host project for static child-operation subtasks.
        /// </summary>
        private global::LocalAutomation.Runtime.OperationParameters CreateProjectBuildAuthoringParameters(
            global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters,
            BuildConfiguration configuration,
            string? additionalArguments = null)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
            parameters.Target = plugin.HostProject;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
            parameters.GetOptions<BuildConfigurationOptions>().Configuration = configuration;
            if (!string.IsNullOrWhiteSpace(additionalArguments))
            {
                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = additionalArguments;
            }

            EnableDeployUbtOptions(parameters);
            return parameters;
        }

        /// <summary>
        /// Creates authoring-time plugin-build parameters from the source plugin for static child-operation subtasks.
        /// </summary>
        private global::LocalAutomation.Runtime.OperationParameters CreatePluginBuildAuthoringParameters(
            global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters,
            UbtCompiler compiler)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
            parameters.Target = plugin;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
            parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
            UbtCompilerOptions compilerOptions = parameters.GetOptions<UbtCompilerOptions>();
            compilerOptions.Compiler = compiler;
            compilerOptions.CppStandard = UbtCppStandard.Default;
            EnableDeployUbtOptions(parameters);
            return parameters;
        }

        /// <summary>
        /// Enables the shared build policy required by every compilation authored by Deploy Plugin.
        /// </summary>
        private static void EnableDeployUbtOptions(global::LocalAutomation.Runtime.OperationParameters parameters)
        {
            parameters.GetOptions<UbtOptions>().NoHotReload = true;
        }

        /// <summary>
        /// Runs the package-only BuildCookRun pass for one prepared example-project variant after its explicit target-build
        /// step has already completed. The staged package output is cleared here so package discovery cannot consume stale
        /// files from an earlier deploy run. Persistent staging and cook roots are also cleared so warm build caches do not
        /// retain package payloads after those outputs have moved into the session workspace.
        /// </summary>
        private Task RunPreparedProjectPackageAsync(Project project, DeploymentWorkspaceState state, string outputPath, BuildConfiguration configuration, global::LocalAutomation.Runtime.ExecutionTaskContext context, string failureMessage, bool noDebugInfo = false)
        {
            // Package and cook output are run-scoped, so clear the whole session package root before UAT writes into it.
            ProjectPackaging.PreparePackageOutputs(project.Model, outputPath, context.Logger, context.CancellationToken);

            // UAT appends the platform under -stagingdirectory but consumes -CookOutputDir as the final platform path.
            string stagingRootPath = ProjectPackaging.GetStagingRootPath(outputPath);
            string cookOutputPath = ProjectPackaging.GetCookOutputPath(outputPath, state.Engine);

            // The prepared project already has editor binaries, so package-only BuildCookRun skips editor compilation explicitly.
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateParameters();
            parameters.Target = project;
            parameters.OutputPathOverride = outputPath;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
            parameters.GetOptions<BuildConfigurationOptions>().Configuration = configuration;

            // The transient operation name keeps the execution graph readable without adding a public deploy-only operation type.
            string operationName = configuration == BuildConfiguration.Shipping ? "Package Demo Project" : "Package Prepared Project";

            return RunChildOperationAsync(
                new ConfiguredBuildCookRunProjectOperation(operationName, PluginDeployment.CreatePreparedProjectPackageRequest(configuration, stagingRootPath, cookOutputPath, noDebugInfo: noDebugInfo)),
                parameters,
                context,
                required: true,
                failureMessage: failureMessage,
                hideChildOperationRootInGraph: true);
        }

        /// <summary>
        /// Runs one package-launch child operation against the already packaged build output for a branch.
        /// </summary>
        private Task RunLaunchPackageAsync(Package package, Engine engine, AutomationOptions automationOptions, global::LocalAutomation.Runtime.ExecutionTaskContext context, string failureMessage, string outputPath)
        {
            // Launch parameters are created at runtime because each branch discovers its packaged executable after staging.
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateParameters();
            parameters.Target = package;
            parameters.OutputPathOverride = outputPath;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
            parameters.SetOptions(automationOptions);
            ApplyValidationLaunchFlags(parameters, editorProcess: false);
            return RunChildOperationAsync<LaunchPackage>(parameters, context, required: true, failureMessage: failureMessage, hideChildOperationRootInGraph: true);
        }

        /// <summary>
        /// Scheduler wrapper for packaging the shared prebuilt project-plugin base with the plugin installed at project
        /// level once its explicit target-build step has finished.
        /// </summary>
        private async Task PackageProjectPluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging project-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project exampleProjectBase = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for project-plugin packaging");
            await RunPreparedProjectPackageAsync(exampleProjectBase, state, state.Layout.ProjectPluginOperationOutputPath, BuildConfiguration.Development, context, "Package project-plugin example failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the packaged project-plugin example.
        /// </summary>
        private async Task TestProjectPluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing project-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Package projectPluginPackage = CreateRequiredSessionPackage(state.Layout.ProjectPluginOperationOutputPath, state, "Project-plugin package output is not available for launch");
            await RunLaunchPackageAsync(projectPluginPackage, state.Engine, automationOptions, context, "Launch and test with project plugin failed", state.Layout.ProjectPluginLaunchOutputPath);
        }

        /// <summary>
        /// Removes an existing engine-installed plugin with the deploy target's name before the package build scans plugins.
        /// </summary>
        private Task RemoveExistingEnginePluginInstallAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Removing existing engine plugin install");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            PluginDeployment.RemoveExistingEnginePluginInstall(state.Engine, state.SourcePlugin.Name, context.Logger, context.CancellationToken);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Scheduler wrapper for installing the built plugin into the engine marketplace folder.
        /// </summary>
        private async Task InstallBuiltPluginToEngineAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Installing built plugin to engine");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Plugin builtPlugin = CreateRequiredPlugin(state.Layout.BuiltPluginPath, "Built plugin is not available for engine installation");
            using Plugin installedPlugin = new(PluginDeployment.InstallBuiltPluginToEngine(
                builtPlugin.Model, state.Engine, context.Logger, context.CancellationToken));
            await Task.CompletedTask;
        }

        /// <summary>
        /// Scheduler wrapper for launching an empty project whose descriptor enables the deployed engine-installed plugin.
        /// </summary>
        private async Task TestEmptyEnginePluginProjectAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing empty engine-plugin project");
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.TestEmptyEnginePluginProject")
                .SetTag("trigger", "StepTransition");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Plugin installedPlugin = CreateRequiredPlugin(state.Layout.InstalledEnginePluginPath, "Engine-installed plugin is not available for empty project launch");
            string emptyProjectPath = state.Layout.EmptyEnginePluginProjectPath;

            // The generated project is run-scoped so each deploy launch starts from a descriptor-only project shell.
            using Project emptyProject = new(PluginDeployment.CreateEmptyEnginePluginProject(
                emptyProjectPath, "EmptyEnginePluginProject", state.Engine, installedPlugin.Name, context.Logger, context.CancellationToken));
            global::LocalAutomation.Runtime.OperationParameters launchEditorParams = CreateParameters();
            launchEditorParams.Target = emptyProject;
            launchEditorParams.OutputPathOverride = state.Layout.EmptyEnginePluginProjectEditorLaunchOutputPath;
            launchEditorParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            launchEditorParams.SetOptions(automationOptions);
            // Empty-project editor validation is a throwaway automation child process.
            ApplyValidationLaunchFlags(launchEditorParams, editorProcess: true);

            await RunChildOperationAsync<LaunchProjectEditor>(launchEditorParams, context, required: true, failureMessage: "Failed to launch empty engine-plugin project in editor", hideChildOperationRootInGraph: true);
            activity.SetTag("result", "Completed");
        }

        /// <summary>
        /// Scheduler wrapper for packaging the engine-plugin example.
        /// </summary>
        private async Task PackageEnginePluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging engine-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project engineVariant = CreateRequiredProject(state.Layout.EnginePluginVariantPath, "Engine-plugin variant is not available for packaging");
            await RunPreparedProjectPackageAsync(engineVariant, state, state.Layout.EnginePluginOperationOutputPath, BuildConfiguration.Development, context, "Package engine-plugin example failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the packaged engine-plugin example.
        /// </summary>
        private async Task TestEnginePluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing engine-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Package enginePluginPackage = CreateRequiredSessionPackage(state.Layout.EnginePluginOperationOutputPath, state, "Engine-plugin package output is not available for launch");
            await RunLaunchPackageAsync(enginePluginPackage, state.Engine, automationOptions, context, "Launch and test engine-plugin example failed", state.Layout.EnginePluginLaunchOutputPath);
        }

        /// <summary>
        /// Scheduler wrapper for packaging the blueprint-only example.
        /// </summary>
        private async Task PackageBlueprintOnlyExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging blueprint-only example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project blueprintVariant = CreateRequiredProject(state.Layout.BlueprintDemoVariantPath, "Blueprint/demo variant is not available for packaging");

            await RunPreparedProjectPackageAsync(blueprintVariant, state, state.Layout.BlueprintOperationOutputPath, BuildConfiguration.Development, context, "Package blueprint-only example failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the copied packaged blueprint-only example snapshot.
        /// </summary>
        private async Task TestBlueprintOnlyExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing blueprint-only example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Package package = CreateRequiredPackage(state.Layout.BlueprintTestPackageSnapshotPath, "Blueprint package test snapshot is not available for launch");
            await RunLaunchPackageAsync(package, state.Engine, automationOptions, context, "Launch and test blueprint-only example failed", state.Layout.BlueprintLaunchOutputPath);
        }

        /// <summary>
        /// Copies the packaged blueprint-only build to a dedicated launch snapshot before the demo package branch recreates
        /// the shared staged-build output for shipping packaging.
        /// </summary>
        private async Task CopyBlueprintPackageForTestAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Copying blueprint package for test");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Package blueprintPackage = CreateRequiredSessionPackage(state.Layout.BlueprintOperationOutputPath, state, "Blueprint package output is not available for snapshot copy");
            string snapshotPath = state.Layout.BlueprintTestPackageSnapshotPath;

            FileUtils.DeleteDirectoryIfExists(snapshotPath);
            DirectoryMaterializer.Copy(blueprintPackage.TargetPath, snapshotPath, cancellationToken: context.CancellationToken);

            using Package snapshotPackage = CreateRequiredPackage(snapshotPath, "Blueprint package test snapshot was not created successfully");
            context.Logger.LogInformation($"Prepared blueprint package test snapshot: {snapshotPackage.TargetPath}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Packages the shipping demo from the prepared blueprint/demo variant so the demo artifact no longer depends on a
        /// mutable shared project-plugin base instance.
        /// </summary>
        private async Task PackageDemoExecutableAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging demo executable");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string demoPackagePath = state.Layout.DemoOperationOutputPath;

            using Project demoVariant = CreateRequiredProject(state.Layout.BlueprintDemoVariantPath, "Blueprint/demo variant is not available for demo packaging");
            await RunPreparedProjectPackageAsync(demoVariant, state, demoPackagePath, BuildConfiguration.Shipping, context, "Package demo executable failed", noDebugInfo: true);
        }

        /// <summary>
        /// Returns one archive zip path that must already exist before the final copy stage runs.
        /// </summary>
        private static string GetRequiredArchiveFile(string archiveZipPath, string failureMessage)
        {
            if (!File.Exists(archiveZipPath))
            {
                throw new FileNotFoundException(failureMessage, archiveZipPath);
            }

            return archiveZipPath;
        }

        /// <summary>
        /// Copies one produced archive zip to the configured archive output directory when the deploy options request an
        /// external archive location.
        /// </summary>
        private static void CopyArchiveToOutputIfConfigured(global::LocalAutomation.Runtime.ExecutionTaskContext context, string archiveZipPath)
        {
            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            string archiveOutputPath = deployOptions.ArchivePath;
            if (string.IsNullOrEmpty(archiveOutputPath))
            {
                return;
            }

            context.Logger.LogInformation($"Copying archive to output path: {archiveOutputPath}");
            Directory.CreateDirectory(archiveOutputPath);
            if (!Directory.Exists(archiveOutputPath))
            {
                throw new Exception($"Could not resolve archive output: {archiveOutputPath}");
            }

            FileUtils.CopyFile(GetRequiredArchiveFile(archiveZipPath, $"Archive zip is missing: {Path.GetFileName(archiveZipPath)}"), archiveOutputPath, true, true);
        }

        /// <summary>
        /// Archives the staged plugin source payload as soon as the staging step has produced the archive-ready copy.
        /// </summary>
        private async Task ArchivePluginSourceAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Archiving plugin source");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Plugin stagingPlugin = CreateRequiredPlugin(state.Layout.StagingPluginPath, "Staged plugin is not available for source archiving");
            string archivePrefix = await BuildArchivePrefixAsync(state);
            string archivePath = Path.Combine(GetOutputPath(context.ValidatedOperationParameters), "Archives");
            string pluginSourceArchiveZipPath = GetArchiveZipPath(context.ValidatedOperationParameters, archivePrefix, "PluginSource.zip");

            Directory.CreateDirectory(archivePath);
            FileUtils.DeleteFileIfExists(pluginSourceArchiveZipPath);
            FileUtils.CreateZipFromDirectory(stagingPlugin.Model.PluginPath, pluginSourceArchiveZipPath, true, context.Logger);
            CopyArchiveToOutputIfConfigured(context, pluginSourceArchiveZipPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Archives the packaged plugin build as soon as the packaged plugin output exists.
        /// </summary>
        private async Task ArchivePluginBuildAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Archiving plugin build");
            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            if (!deployOptions.ArchivePluginBuild)
            {
                await Task.CompletedTask;
                return;
            }

            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Plugin builtPlugin = CreateRequiredPlugin(state.Layout.BuiltPluginPath, "Built plugin is not available for archiving");
            string archivePrefix = await BuildArchivePrefixAsync(state);
            string archivePath = Path.Combine(GetOutputPath(context.ValidatedOperationParameters), "Archives");
            string pluginBuildZipPath = GetArchiveZipPath(context.ValidatedOperationParameters, archivePrefix, "PluginBuild.zip");

            Directory.CreateDirectory(archivePath);
            FileUtils.DeleteFileIfExists(pluginBuildZipPath);
            FileUtils.CreateZipFromDirectory(builtPlugin.Model.PluginPath, pluginBuildZipPath, true, context.Logger);
            CopyArchiveToOutputIfConfigured(context, pluginBuildZipPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Archives the example-project payload from a dedicated archive copy so archive pruning never mutates the live
        /// blueprint/demo variant used by package tasks.
        /// </summary>
        private async Task ArchiveExampleProjectAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Archiving example project");
            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            if (!deployOptions.ArchiveExampleProject)
            {
                await Task.CompletedTask;
                return;
            }

            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string archiveProjectPath = state.Layout.ExampleArchiveProjectPath;
            string archivePrefix = await BuildArchivePrefixAsync(state);
            string exampleProjectZipPath = GetArchiveZipPath(context.ValidatedOperationParameters, archivePrefix, "ExampleProject.zip");

            using Project blueprintVariant = CreateRequiredProject(state.Layout.BlueprintDemoVariantPath, "Blueprint/demo variant is not available for archive materialization");
            PluginDeployment.CreateExampleProjectArchive(blueprintVariant.Model, archiveProjectPath, exampleProjectZipPath,
                context.Logger, context.CancellationToken);
            CopyArchiveToOutputIfConfigured(context, exampleProjectZipPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Archives the demo package as soon as the packaged demo output exists.
        /// </summary>
        private async Task ArchiveDemoPackageAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Archiving demo package");
            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            if (!deployOptions.ArchiveDemoPackage)
            {
                await Task.CompletedTask;
                return;
            }

            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Package demoPackage = CreateRequiredSessionPackage(state.Layout.DemoOperationOutputPath, state, "Demo package output is not available for archiving");
            string archivePrefix = await BuildArchivePrefixAsync(state);
            string archivePath = Path.Combine(GetOutputPath(context.ValidatedOperationParameters), "Archives");
            string demoPackageZipPath = GetArchiveZipPath(context.ValidatedOperationParameters, archivePrefix, "DemoPackage.zip");

            Directory.CreateDirectory(archivePath);
            FileUtils.DeleteFileIfExists(demoPackageZipPath);
            FileUtils.CreateZipFromDirectory(demoPackage.TargetPath, demoPackageZipPath, false, context.Logger);
            CopyArchiveToOutputIfConfigured(context, demoPackageZipPath);
            await Task.CompletedTask;
        }
    }

    [Operation(SortOrder = 10)]
    public class DeployPlugin : UnrealOperation<Plugin>
    {
        protected override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            EngineVersionOptions engineVersionOptions = operationParameters.GetOptions<EngineVersionOptions>();
            if (engineVersionOptions.EnabledVersions.Count == 0)
            {
                return null;
            }

            foreach (EngineVersion engineVersion in engineVersionOptions.EnabledVersions)
            {
                Engine? engine = EngineFinder.GetEngineInstall(engineVersion);
                if (engine == null)
                {
                    return $"Engine {engineVersion.MajorMinorString} not found";
                }

                PluginBuildOptions platforms = operationParameters.GetOptions<PluginBuildOptions>();
                string? platformRequirementsError = PluginBuildPlatformValidation.CheckRequirementsSatisfied(engine,
                    PluginBuildPlatformValidation.GetRequestedTargetPlatforms(platforms.BuildWin64, platforms.BuildLinux,
                        operationParameters.GetOptions<AdditionalArgumentsOptions>().Arguments));
                if (platformRequirementsError != null)
                {
                    return $"Engine {engineVersion.MajorMinorString}: {platformRequirementsError}";
                }
            }

            return null;
        }

        /// <summary>
        /// Describes the deploy-plugin subtree beneath the framework-owned root task.
        /// </summary>
        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            IReadOnlyList<EngineVersion> enabledVersions = operationParameters.GetOptions<EngineVersionOptions>().EnabledVersions;
            List<EngineVersion> targetVersions = enabledVersions.Count > 0
                ? enabledVersions.ToList()
                : new List<EngineVersion> { plugin.EngineInstance.Version };

            /* Shared source preparation is an authored deploy step rather than an implicit callback on the root so it
               stays visible in the graph and remains the explicit predecessor of the per-engine branches. */
            root.Child("Prepare Shared Source", "Apply shared source-tree mutations once before engine-specific workspaces are created")
                .WithRetry(WorkspaceFileLockRetryPolicy.Instance)
                .Run(PrepareSharedSourceAsync);

            root.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, engines =>
            {
                foreach (EngineVersion engineVersion in targetVersions)
                {
                    engines.AddChildOperation<DeployPluginForEngine>(
                        $"UE {engineVersion.MajorMinorString}",
                        () =>
                        {
                            // Each authored child targets exactly one engine so nested Unreal operations inherit a single version.
                            global::LocalAutomation.Runtime.OperationParameters childParameters = operationParameters.CreateChild();
                            childParameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engineVersion };
                            return childParameters;
                        },
                        "Per-engine deployment scope");
                }
            });
        }

        /// <summary>Updates shared source versions and copyright before isolated per-engine workspaces are copied.</summary>
        private async Task PrepareSharedSourceAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing shared plugin source");
            global::LocalAutomation.Runtime.ValidatedOperationParameters validatedParameters = context.ValidatedOperationParameters;
            Plugin plugin = GetRequiredTarget(validatedParameters);
            Project hostProject = plugin.HostProject;
            PluginDeployment.PrepareSharedSource(plugin.Model, hostProject.Model, context.Logger, context.CancellationToken);
            context.SetOperationData(new DeployPreparedSourceState(plugin, hostProject));
            await Task.CompletedTask;
        }

        /// <summary>
        /// Plugin deployment exposes engine selection, automation toggles, plugin build settings, command pass-through
        /// arguments, and deployment packaging controls so the user can configure the full archive/test flow up front.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(EngineVersionOptions),
                    typeof(AutomationOptions),
                    typeof(PluginBuildOptions),
                    typeof(AdditionalArgumentsOptions),
                    typeof(PluginDeployOptions)
                });
        }

    }
}
