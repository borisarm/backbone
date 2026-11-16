using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Backbone.Contracts;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.IntegrationTests.Persistence;

/// <summary>
/// HU-04 criterios 1 y 3 con la política de producción (<see cref="FileTrustPolicy.Default"/>, ADR-07, ADR-14): el archivo de
/// credenciales queda con propietario Administradores y la DACL protegida de RNF-07 (solo SYSTEM y Administradores), cifrado
/// con DPAPI de ámbito equipo, y otra instancia del almacén (como tras reiniciar el servicio) lo vuelve a leer. Solo elevado
/// se puede asignar Administradores como propietario. Requiere PowerShell elevado.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Story", "HU-04")]
public sealed class CredentialStoreElevatedTests : IDisposable
{
    private const string NeedsElevation = "Requires an elevated process (Administrators as owner).";
    private const string Share = @"\\nas\backups";
    private const string Password = "Contraseña-elevada-7c21";

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    private readonly TempDirectory _temp = new();

    public static bool IsElevated => WindowsTestEnvironment.IsElevated;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task The_credential_file_has_the_RNF07_DACL_and_another_store_instance_reads_it_back()
    {
        Directory.CreateDirectory(_temp.Combine("credentials")); // la crea el servicio al arrancar (§8.3)

        using (var saving = CreateStore())
        {
            await saving.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct);
        }

        using var store = CreateStore();
        var file = Assert.Single(Directory.GetFiles(_temp.Combine("credentials")));
        Assert.Equal(store.PathFor(Share), file);

        var security = new FileInfo(file).GetAccessControl();
        Assert.Equal(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, r => Assert.Equal((AccessControlType.Allow, FileSystemRights.FullControl, false), (r.AccessControlType, r.FileSystemRights, r.IsInherited)));
        Assert.True(rules.Select(r => (SecurityIdentifier)r.IdentityReference).ToHashSet().SetEquals([LocalSystem, Administrators]));

        var info = await store.GetInfoAsync(Share, Ct);
        Assert.Equal("ana", info?.UserName);
        using var credential = await store.GetAsync(Share, Ct);
        Assert.NotNull(credential);
        Assert.Equal(Password, credential.Password.ToString());

        // Criterio 3: ni en claro, ni en UTF-16, ni en Base64, en ningún archivo de la carpeta de datos.
        var utf8 = Encoding.UTF8.GetBytes(Password);
        foreach (var path in Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories))
        {
            var content = await File.ReadAllBytesAsync(path, Ct);
            Assert.True(content.AsSpan().IndexOf(utf8) < 0);
            Assert.True(content.AsSpan().IndexOf(Encoding.Unicode.GetBytes(Password)) < 0);
            Assert.True(content.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Convert.ToBase64String(utf8))) < 0);
        }
    }

    private CredentialStore CreateStore() =>
        new(_temp.Combine("credentials"), new TrustedFile(FileTrustPolicy.Default), new DpapiMachineProtector(), TimeProvider.System,
            NullLogger<CredentialStore>.Instance);
}
