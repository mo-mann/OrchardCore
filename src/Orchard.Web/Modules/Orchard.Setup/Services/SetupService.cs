using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Orchard.Data.Migration;
using Orchard.DeferredTasks;
using Orchard.Environment.Extensions;
using Orchard.Environment.Shell;
using Orchard.Environment.Shell.Builders;
using Orchard.Environment.Shell.Descriptor;
using Orchard.Environment.Shell.Descriptor.Models;
using Orchard.Environment.Shell.Models;
using Orchard.Events;
using Orchard.Hosting;
using Orchard.Hosting.ShellBuilders;
using Orchard.Recipes.Models;
using Orchard.Recipes.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YesSql.Core.Services;
using System;

namespace Orchard.Setup.Services
{
    public class SetupService : ISetupService
    {
        private readonly ShellSettings _shellSettings;
        private readonly IOrchardHost _orchardHost;
        private readonly IShellContextFactory _shellContextFactory;
        private readonly ICompositionStrategy _compositionStrategy;
        private readonly IExtensionManager _extensionManager;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IRunningShellTable _runningShellTable;
        private readonly IRecipeHarvester _recipeHarvester;
        private readonly ILogger _logger;

        private IReadOnlyList<RecipeDescriptor> _recipes;

        public SetupService(
            ShellSettings shellSettings,
            IOrchardHost orchardHost,
            IShellContextFactory shellContextFactory,
            ICompositionStrategy compositionStrategy,
            IExtensionManager extensionManager,
            IHttpContextAccessor httpContextAccessor,
            IRunningShellTable runningShellTable,
            IRecipeHarvester recipeHarvester,
            ILogger<SetupService> logger
            )
        {
            _shellSettings = shellSettings;
            _orchardHost = orchardHost;
            _shellContextFactory = shellContextFactory;
            _compositionStrategy = compositionStrategy;
            _extensionManager = extensionManager;
            _httpContextAccessor = httpContextAccessor;
            _runningShellTable = runningShellTable;
            _recipeHarvester = recipeHarvester;
            _logger = logger;
        }

        public ShellSettings Prime()
        {
            return _shellSettings;
        }

        public async Task<IEnumerable<RecipeDescriptor>> GetSetupRecipesAsync()
        {
            if (_recipes == null)
            {
                _recipes = (await _recipeHarvester.HarvestRecipesAsync())
                    .Where(recipe => recipe.IsSetupRecipe)
                    .ToList();
            }

            return _recipes;
        }

        public async Task<string> SetupAsync(SetupContext context)
        {
            var initialState = _shellSettings.State;
            try
            {
                return await SetupInternalAsync(context);
            }
            catch
            {
                _shellSettings.State = initialState;
                throw;
            }
        }

        public async Task<string> SetupInternalAsync(SetupContext context)
        {
            string executionId;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Running setup for tenant '{0}'.", _shellSettings.Name);
            }

            // Features to enable for Setup
            string[] hardcoded =
            {
                "Orchard.Hosting", // shortcut for built-in features
                "Orchard.Modules",
                "Orchard.Recipes"
            };

            context.EnabledFeatures = hardcoded.Union(context.EnabledFeatures ?? Enumerable.Empty<string>()).Distinct().ToList();

            // Set shell state to "Initializing" so that subsequent HTTP requests are responded to with "Service Unavailable" while Orchard is setting up.
            _shellSettings.State = TenantState.Initializing;

            var shellSettings = new ShellSettings(_shellSettings);

            if (string.IsNullOrEmpty(shellSettings.DatabaseProvider))
            {
                shellSettings.DatabaseProvider = context.DatabaseProvider;
                shellSettings.ConnectionString = context.DatabaseConnectionString;
                shellSettings.TablePrefix = context.DatabaseTablePrefix;
            }

            // Creating a standalone environment based on a "minimum shell descriptor".
            // In theory this environment can be used to resolve any normal components by interface, and those
            // components will exist entirely in isolation - no crossover between the safemode container currently in effect
            // It is used to initialize the database before the recipe is run.

            var shellDescriptor = new ShellDescriptor
            {
                Features = context.EnabledFeatures.Select(name => new ShellFeature { Name = name }).ToList()
            };

            using (var shellContext = _shellContextFactory.CreateDescribedContext(shellSettings, shellDescriptor))
            {
                using (var scope = shellContext.CreateServiceScope())
                {
                    var store = scope.ServiceProvider.GetRequiredService<IStore>();

                    try
                    {
                        await store.InitializeAsync();
                    }
                    catch
                    {
                        // Tables already exist or database was not found

                        // The issue is that the user creation needs the tables to be present,
                        // if the user information is not valid, the next POST will try to recreate the
                        // tables. The tables should be rollbacked if one of the steps is invalid,
                        // unless the recipe is executing?
                    }

                    // Create the "minimum shell descriptor"
                    await scope
                        .ServiceProvider
                        .GetService<IShellDescriptorManager>()
                        .UpdateShellDescriptorAsync(0,
                            shellContext.Blueprint.Descriptor.Features,
                            shellContext.Blueprint.Descriptor.Parameters);

                    // Apply all migrations for the newly initialized tenant
                    var dataMigrationManager = scope.ServiceProvider.GetService<IDataMigrationManager>();
                    await dataMigrationManager.UpdateAllFeaturesAsync();

                    var deferredTaskEngine = scope.ServiceProvider.GetService<IDeferredTaskEngine>();

                    if (deferredTaskEngine != null && deferredTaskEngine.HasPendingTasks)
                    {
                        var taskContext = new DeferredTaskContext(scope.ServiceProvider);
                        await deferredTaskEngine.ExecuteTasksAsync(taskContext);
                    }
                }

                _orchardHost.UpdateShellSettings(shellSettings);

                executionId = Guid.NewGuid().ToString("n");

                // Create a new scope for the recipe thread to prevent race issues with other scoped
                // services from the request.
                using (var scope = shellContext.CreateServiceScope())
                {
                    var recipeExecutor = scope.ServiceProvider.GetService<IRecipeExecutor>();

                    // Right now we run the recipe in the same thread, later use polling from the setup screen
                    // to query the current execution.
                    //await Task.Run(async () =>
                    //{
                    await recipeExecutor.ExecuteAsync(executionId, context.Recipe);
                    //});

                    var deferredTaskEngine = scope.ServiceProvider.GetService<IDeferredTaskEngine>();

                    // The recipe might have added some deferred tasks to process
                    if (deferredTaskEngine != null && deferredTaskEngine.HasPendingTasks)
                    {
                        var taskContext = new DeferredTaskContext(scope.ServiceProvider);
                        await deferredTaskEngine.ExecuteTasksAsync(taskContext);
                    }
                }
            }

            // Reloading the shell context as the recipe  has probably updated its features
            using (var shellContext = _orchardHost.CreateShellContext(shellSettings))
            {
                using (var scope = shellContext.CreateServiceScope())
                {
                    // Apply all migrations for the newly initialized tenant
                    var dataMigrationManager = scope.ServiceProvider.GetService<IDataMigrationManager>();
                    await dataMigrationManager.UpdateAllFeaturesAsync();

                    bool hasErrors = false;

                    Action<string, string> reportError = (key, message) => {
                        hasErrors = true;
                        context.Errors[key] = message;
                    };

                    // Invoke modules to react to the setup event
                    var eventBus = scope.ServiceProvider.GetService<IEventBus>();
                    await eventBus.NotifyAsync<ISetupEventHandler>(x => x.Setup(
                        context.SiteName,
                        context.AdminUsername,
                        context.AdminEmail,
                        context.AdminPassword,
                        context.DatabaseProvider,
                        context.DatabaseConnectionString,
                        context.DatabaseTablePrefix,
                        reportError
                    ));

                    if (hasErrors)
                    {
                        // TODO: check why the tables creation is not reverted
                        var session = scope.ServiceProvider.GetService<YesSql.Core.Services.ISession>();
                        session.Cancel();

                        return executionId;
                    }

                    var deferredTaskEngine = scope.ServiceProvider.GetService<IDeferredTaskEngine>();

                    if (deferredTaskEngine != null && deferredTaskEngine.HasPendingTasks)
                    {
                        var taskContext = new DeferredTaskContext(scope.ServiceProvider);
                        await deferredTaskEngine.ExecuteTasksAsync(taskContext);
                    }
                }

                // Update the shell state
                shellSettings.State = TenantState.Running;
                _orchardHost.UpdateShellSettings(shellSettings);
            }


            return executionId;
        }
    }
}