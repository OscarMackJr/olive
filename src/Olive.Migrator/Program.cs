using Olive.Persistence;

return await MigratorCommand.RunAsync(
    Environment.GetEnvironmentVariable("OLIVE_MIGRATOR_URL"),
    MigrationCatalog.LoadEmbedded(),
    Console.Out,
    CancellationToken.None);
