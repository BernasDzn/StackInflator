using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public class Program
{
	public static async Task Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);
		builder.Logging.ClearProviders();
		builder.Logging.AddConsole();

		// Swagger / OpenAPI
		builder.Services.AddEndpointsApiExplorer();
		builder.Services.AddSwaggerGen();

		builder.Services.AddSingleton<StackInflatorService>();

		var app = builder.Build();

		var inflator = app.Services.GetRequiredService<StackInflatorService>();

		// If CLI args are provided, run in CLI mode and exit without starting the web server.
		if (args != null && args.Length > 0)
		{
			var cmd = args[0].ToLowerInvariant();
			var logger = app.Services.GetRequiredService<ILogger<Program>>();

			switch (cmd)
			{
				case "inflate":
				{
					int max = 1024;
					int step = 10;
					foreach (var a in args)
					{
						if (a.StartsWith("--maxMb=", StringComparison.OrdinalIgnoreCase))
							int.TryParse(a.Substring("--maxMb=".Length), out max);
						if (a.StartsWith("--stepMb=", StringComparison.OrdinalIgnoreCase))
							int.TryParse(a.Substring("--stepMb=".Length), out step);
					}

					logger.LogInformation("Starting CLI inflation to {Max} MB in steps of {Step} MB", max, step);
					await inflator.InflateAsync(max, step, logger);
					Console.WriteLine($"Inflation complete: {inflator.AllocatedMb} MB allocated");
					return;
				}

				case "status":
				{
					Console.WriteLine($"AllocatedMB: {inflator.AllocatedMb}, Blocks: {inflator.BlockCount}");
					return;
				}

				case "reset":
				{
					inflator.Reset(logger);
					Console.WriteLine("Reset allocation to 0 MB");
					return;
				}

				case "help":
				default:
				{
					Console.WriteLine("Usage: stackinflator [command] [--maxMb=NUMBER] [--stepMb=NUMBER]");
					Console.WriteLine("Commands:");
					Console.WriteLine("  inflate    Allocate memory (commands run synchronously in CLI)");
					Console.WriteLine("  status     Show current allocation");
					Console.WriteLine("  reset      Free allocated memory");
					return;
				}
			}
		}

		app.MapPost("/inflate", (int? maxMb, int? stepMb, ILogger<Program> logger) =>
		{
			// defaults: 1GB max, 10MB steps
			int max = maxMb ?? 1024;
			int step = stepMb ?? 10;

			// run the inflation in background so the HTTP request returns quickly
			_ = Task.Run(() => inflator.InflateAsync(max, step, logger));

			return Results.Accepted($"/status", $"Inflation started to {max} MB in steps of {step} MB");
		})
		.WithName("InflateStack")
		.WithTags("StackInflator")
		.Produces(StatusCodes.Status202Accepted);

		app.MapGet("/status", () => Results.Ok(new { allocatedMb = inflator.AllocatedMb, blocks = inflator.BlockCount }))
			.WithName("GetStatus")
			.WithTags("StackInflator")
			.Produces(200);

		app.MapPost("/reset", (ILogger<Program> logger) =>
		{
			inflator.Reset(logger);
			return Results.Ok(new { allocatedMb = inflator.AllocatedMb });
		})
		.WithName("Reset")
		.WithTags("StackInflator")
		.Produces(200);

		app.MapPost("/stop", (ILogger<Program> logger) =>
		{
			logger.LogInformation("Stop requested via /stop endpoint. Shutting down.");
			app.Lifetime.StopApplication();
			return Results.Ok(new { stopping = true });
		})
		.WithName("Stop")
		.WithTags("StackInflator")
		.Produces(200);

		// Enable middleware for Swagger UI
		app.UseSwagger();
		app.UseSwaggerUI();

		await app.RunAsync();
	}
}
