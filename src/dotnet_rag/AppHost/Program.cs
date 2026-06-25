var builder = DistributedApplication.CreateBuilder(args);

var blazorFrontend = builder.AddProject<Projects.DotnetRag_Blazor>("blazor-frontend");

await builder.Build().RunAsync();
