//////////////////////////////////////////////////////////////////////
// ADDINS
//////////////////////////////////////////////////////////////////////

#addin "nuget:?package=MagicChunks&version=1.1.0.34"
#addin "nuget:?package=Cake.Tfx&version=0.4.2"
#addin "nuget:?package=Cake.Npm&version=0.7.2"
#addin "nuget:?package=Cake.AppVeyor&version=1.1.0.9"

//////////////////////////////////////////////////////////////////////
// TOOLS
//////////////////////////////////////////////////////////////////////

#tool "nuget:?package=gitreleasemanager&version=0.7.1"
#tool "nuget:?package=GitVersion.CommandLine&version=3.6.4"

// Load other scripts.
#load "./build/parameters.cake"

//////////////////////////////////////////////////////////////////////
// PARAMETERS
//////////////////////////////////////////////////////////////////////

BuildParameters parameters = BuildParameters.GetParameters(Context, BuildSystem);
bool publishingError = false;

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(context =>
{
    parameters.SetBuildVersion(
        BuildVersion.CalculatingSemanticVersion(
            context: Context,
            parameters: parameters
        )
    );

    // Increase verbosity?
    if(parameters.IsMasterBranch && (context.Log.Verbosity != Verbosity.Diagnostic)) {
        Information("Increasing verbosity to diagnostic.");
        context.Log.Verbosity = Verbosity.Diagnostic;
    }

    Information("Building version {0} of chocolatey-azuredevops ({1}, {2}) using version {3} of Cake. (IsTagged: {4})",
        parameters.Version.SemVersion,
        parameters.Configuration,
        parameters.Target,
        parameters.Version.CakeVersion,
        parameters.IsTagged);
});

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
{
    CleanDirectories(new[] { "./build-results" });
});

Task("Install-Tfx-Cli")
    .Does(() =>
{
    Npm.WithLogLevel(NpmLogLevel.Silent).FromPath(".").Install(settings => settings.Package("tfx-cli").Globally());
});

Task("Create-Release-Notes")
    .Does(() =>
{
    GitReleaseManagerCreate(parameters.GitHub.UserName, parameters.GitHub.Password, "gep13", "chocolatey-azuredevops", new GitReleaseManagerCreateSettings {
        Milestone         = parameters.Version.Milestone,
        Name              = parameters.Version.Milestone,
        Prerelease        = true,
        TargetCommitish   = "master"
    });
});

Task("Update-Json-Versions")
    .Does(() =>
{
    var projectToPackagePackageJson = "vss-extension.json";
    Information("Updating {0} version -> {1}", projectToPackagePackageJson, parameters.Version.SemVersion);

    TransformConfig(projectToPackagePackageJson, projectToPackagePackageJson, new TransformationCollection {
        { "version", parameters.Version.SemVersion }
    });

    var taskJson = "Tasks/chocolatey/task.json";
    Information("Updating {0} version -> {1}", taskJson, parameters.Version.SemVersion);

    TransformConfig(taskJson, taskJson, new TransformationCollection {
        { "version/Major", parameters.Version.Major }
    });

    TransformConfig(taskJson, taskJson, new TransformationCollection {
        { "version/Minor", parameters.Version.Minor }
    });

    TransformConfig(taskJson, taskJson, new TransformationCollection {
        { "version/Patch", parameters.Version.Patch }
    });
});

Task("Package-Extension")
    .IsDependentOn("Update-Json-Versions")
    .IsDependentOn("Install-Tfx-Cli")
    .IsDependentOn("Clean")
    .Does(() =>
{
    var buildResultDir = Directory("./build-results");

    TfxExtensionCreate(new TfxExtensionCreateSettings()
    {
        ManifestGlobs = new List<string>(){ "./vss-extension.json" },
        OutputPath = buildResultDir
    });
});

Task("Upload-AppVeyor-Artifacts")
    .IsDependentOn("Package-Extension")
    .WithCriteria(() => parameters.IsRunningOnAppVeyor)
.Does(() =>
{
    var buildResultDir = Directory("./build-results");
    var packageFile = File("gep13.chocolatey-azuredevops-" + parameters.Version.SemVersion + ".vsix");
    AppVeyor.UploadArtifact(buildResultDir + packageFile);
});

Task("Publish-GitHub-Release")
    .WithCriteria(() => parameters.ShouldPublish)
    .Does(() =>
{
    var buildResultDir = Directory("./build-results");
    var packageFile = File("gep13.chocolatey-azuredevops-" + parameters.Version.SemVersion + ".vsix");

    GitReleaseManagerAddAssets(parameters.GitHub.UserName, parameters.GitHub.Password, "gep13", "chocolatey-azuredevops", parameters.Version.Milestone, buildResultDir + packageFile);
    GitReleaseManagerClose(parameters.GitHub.UserName, parameters.GitHub.Password, "gep13", "chocolatey-azuredevops", parameters.Version.Milestone);
})
.OnError(exception =>
{
    Information("Publish-GitHub-Release Task failed, but continuing with next Task...");
    publishingError = true;
});

Task("Publish-Extension")
    .IsDependentOn("Package-Extension")
    .WithCriteria(() => parameters.ShouldPublish)
    .Does(() =>
{
    var buildResultDir = Directory("./build-results");
    var packageFile = File("gep13.chocolatey-azuredevops-" + parameters.Version.SemVersion + ".vsix");

    TfxExtensionPublish(buildResultDir + packageFile, new TfxExtensionPublishSettings()
    {
        AuthType = TfxAuthType.Pat,
        Token = parameters.Marketplace.Token
    });
})
.OnError(exception =>
{
    Information("Publish-Extension Task failed, but continuing with next Task...");
    publishingError = true;
});

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Default")
    .IsDependentOn("Package-Extension");

Task("Appveyor")
    .IsDependentOn("Upload-AppVeyor-Artifacts")
    .IsDependentOn("Publish-Extension")
    .IsDependentOn("Publish-GitHub-Release")
    .Finally(() =>
{
    if(publishingError)
    {
        throw new Exception("An error occurred during the publishing of cake-vscode.  All publishing tasks have been attempted.");
    }
});

Task("ReleaseNotes")
  .IsDependentOn("Create-Release-Notes");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(parameters.Target);
