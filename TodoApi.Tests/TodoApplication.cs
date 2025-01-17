﻿using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TodoApi.Tests;
using Xunit;

internal class TodoApplication : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _sqliteConnection = new("Filename=:memory:");

    public TodoDbContext CreateTodoDbContext()
    {
        var db = Services.GetRequiredService<IDbContextFactory<TodoDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
        return db;
    }

    public HttpClient CreateClient(string id, bool isAdmin = false)
    {
        return CreateDefaultClient(new AuthHandler(req =>
        {
            var token = CreateToken(id, isAdmin);
            req.Headers.Authorization = new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, token);
        }));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Open the connection, this creates the SQLite in-memory database, which will persist until the connection is closed
        _sqliteConnection.Open();

        builder.ConfigureServices(services =>
        {
            // We're going to use the factory from our tests
            services.AddDbContextFactory<TodoDbContext>();

            // We need to replace the configuration for the DbContext to use a different configured database
            services.AddDbContextOptions<TodoDbContext>(o => o.UseSqlite(_sqliteConnection));
        });

        // We need to configure signing keys for CI scenarios where
        // there's no user-jwts tool
        var keyBytes = new byte[32];
        RandomNumberGenerator.Fill(keyBytes);
        var base64Key = Convert.ToBase64String(keyBytes);

        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = "dotnet-user-jwts",
                ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = base64Key
            });
        });

        return base.CreateHost(builder);
    }

    private string CreateToken(string id, bool isAdmin = false)
    {
        // Read the user JWTs configuration for testing so unit tests can generate
        // JWT tokens.

        var configuration = Services.GetRequiredService<IConfiguration>();
        var bearerSection = configuration.GetSection("Authentication:Schemes:Bearer");
        var section = bearerSection.GetSection("SigningKeys:0");
        var issuer = section["Issuer"];
        var signingKeyBase64 = section["Value"];

        Assert.NotNull(issuer);
        Assert.NotNull(signingKeyBase64);

        var signingKeyBytes = Convert.FromBase64String(signingKeyBase64);

        var audiences = bearerSection.GetSection("ValidAudiences").GetChildren().Select(s =>
        {
            var audience = s.Value;
            Assert.NotNull(audience);
            return audience;
        }).ToList();

        var jwtIssuer = new JwtIssuer(issuer, signingKeyBytes);

        var roles = new List<string>();

        if (isAdmin)
        {
            roles.Add("admin");
        }

        var token = jwtIssuer.Create(new(
            JwtBearerDefaults.AuthenticationScheme,
            Name: Guid.NewGuid().ToString(),
            Audiences: audiences,
            Issuer: jwtIssuer.Issuer,
            NotBefore: DateTime.UtcNow,
            ExpiresOn: DateTime.UtcNow.AddDays(1),
            Roles: roles,
            Scopes: new List<string> { },
            Claims: new Dictionary<string, string> { ["id"] = id }));

        return JwtIssuer.WriteToken(token);
    }

    protected override void Dispose(bool disposing)
    {
        _sqliteConnection?.Dispose();
        base.Dispose(disposing);
    }

    private sealed class AuthHandler : DelegatingHandler
    {
        private readonly Action<HttpRequestMessage> _onRequest;

        public AuthHandler(Action<HttpRequestMessage> onRequest)
        {
            _onRequest = onRequest;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _onRequest(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}