using System.Text;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Documents;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.Documents;
using JiranisokoTech.Infrastructure.Settings;
using JiranisokoTech.Tests.Infrastructure;
using Docs = JiranisokoTech.Application.Documents.Documents;

namespace JiranisokoTech.Tests.Documents;

/// <summary>
/// Attachments: what is accepted, where the bytes go, and who may read them.
/// </summary>
public class AttachmentTests
{
    /// <summary>
    /// A store that keeps files in memory, so the tests touch no disk.
    /// </summary>
    /// <remarks>
    /// It repeats the real store's one rule — the name on disk is generated,
    /// never the name that arrived — because a test double that accepted the
    /// original name would let a test pass that the real store would refuse.
    /// </remarks>
    private sealed class InMemoryStore : IDocumentStore
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public int Deleted { get; private set; }

        public Task<string> SaveAsync(
            Stream contents, string originalName, CancellationToken cancellationToken = default)
        {
            var extension = Path.GetExtension(originalName);

            if (!Docs.Extensions.Contains(extension))
            {
                throw new InvalidOperationException("That is not a document this system accepts.");
            }

            using var buffer = new MemoryStream();
            contents.CopyTo(buffer);

            var stored = $"{Guid.CreateVersion7():N}{extension.ToLowerInvariant()}";
            Files[stored] = buffer.ToArray();

            return Task.FromResult(stored);
        }

        public Task<Stream?> OpenAsync(
            string storedName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(
                Files.TryGetValue(storedName, out var bytes) ? new MemoryStream(bytes) : null);

        public Task DeleteAsync(string storedName, CancellationToken cancellationToken = default)
        {
            if (Files.Remove(storedName))
            {
                Deleted++;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        public InMemoryStore Store { get; } = new();

        public ClientService Clients => new(new BusinessRepository(_context));

        public InvoiceService Invoices => new(
            new BusinessRepository(_context),
            new SettingsService(new SettingsRepository(_context)),
            db.Clock);

        public DocumentService Documents =>
            new(new AttachmentRepository(_context), Store, db.Clock);

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }

    private static Stream Contents(string text = "a contract") =>
        new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task A_document_is_attached_and_can_be_read_back()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        await using var contents = Contents();

        var attached = await module.Documents.AttachAsync(
            AttachedTo.Client, client.Id, contents, "contract.pdf", 10, null, "The signed one");

        Assert.Equal("contract.pdf", attached.FileName);
        Assert.NotEqual("contract.pdf", attached.StoredName);

        var found = await module.Documents.ForAsync(AttachedTo.Client, client.Id);

        Assert.Single(found);
        Assert.Equal("The signed one", found[0].Note);
    }

    /// <summary>
    /// The name on disk is never the name that arrived.
    /// </summary>
    /// <remarks>
    /// Three things at once: two uploads of "scan.pdf" cannot overwrite each
    /// other, a name like "../../appsettings.json" cannot escape the folder,
    /// and the filename on disk says nothing about what is in it.
    /// </remarks>
    [Fact]
    public async Task Two_files_of_the_same_name_do_not_collide()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        await using var first = Contents("the first");
        var one = await module.Documents.AttachAsync(
            AttachedTo.Client, client.Id, first, "scan.pdf", 9, null);

        await using var second = Contents("the second");
        var two = await module.Documents.AttachAsync(
            AttachedTo.Client, client.Id, second, "scan.pdf", 10, null);

        Assert.NotEqual(one.StoredName, two.StoredName);
        Assert.Equal(2, module.Store.Files.Count);
    }

    [Theory]
    [InlineData("payload.exe")]
    [InlineData("script.sh")]
    [InlineData("nothing")]
    [InlineData("../../appsettings.json")]
    public async Task A_file_that_is_not_a_document_is_refused(string name)
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        await using var contents = Contents();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Documents.AttachAsync(
                AttachedTo.Client, client.Id, contents, name, 10, null));

        Assert.Empty(module.Store.Files);
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        await using var contents = Contents();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Documents.AttachAsync(
                AttachedTo.Client, client.Id, contents, "huge.pdf", Docs.MaximumBytes + 1, null));

        Assert.Contains("larger than", refused.Message);
    }

    /// <summary>
    /// Attaching to something that does not exist is refused before anything is
    /// written.
    /// </summary>
    /// <remarks>
    /// The owner is not a foreign key — it points at one of five tables
    /// depending on the kind — so the database cannot refuse this. Without the
    /// check the result is a file on disk and a row nothing will ever show.
    /// </remarks>
    [Fact]
    public async Task Attaching_to_something_that_does_not_exist_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await using var contents = Contents();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Documents.AttachAsync(
                AttachedTo.Client, Guid.CreateVersion7(), contents, "contract.pdf", 10, null));

        Assert.Empty(module.Store.Files);
    }

    /// <summary>
    /// Attachments on one thing are not attachments on another.
    /// </summary>
    [Fact]
    public async Task An_attachment_belongs_to_one_owner_of_one_kind()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var invoice = await module.Invoices.DraftAsync(client.Id);

        await using var one = Contents();
        await module.Documents.AttachAsync(
            AttachedTo.Client, client.Id, one, "contract.pdf", 10, null);

        await using var two = Contents();
        await module.Documents.AttachAsync(
            AttachedTo.Invoice, invoice.Id, two, "order.pdf", 10, null);

        Assert.Single(await module.Documents.ForAsync(AttachedTo.Client, client.Id));
        Assert.Single(await module.Documents.ForAsync(AttachedTo.Invoice, invoice.Id));

        // The same identifier under a different kind finds nothing, which is
        // what stops a guessed id reaching across tables.
        Assert.Empty(await module.Documents.ForAsync(AttachedTo.Invoice, client.Id));
    }

    [Fact]
    public async Task Removing_an_attachment_takes_the_file_with_it()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        await using var contents = Contents();
        var attached = await module.Documents.AttachAsync(
            AttachedTo.Client, client.Id, contents, "contract.pdf", 10, null);

        await module.Documents.RemoveAsync(attached.Id);

        Assert.Empty(await module.Documents.ForAsync(AttachedTo.Client, client.Id));
        Assert.Empty(module.Store.Files);
        Assert.Equal(1, module.Store.Deleted);
    }

    /// <summary>
    /// Every kind of attachment has a permission, for reading and for attaching.
    /// </summary>
    /// <remarks>
    /// The table is read by both the upload and the download, so a kind missing
    /// from it would throw at the moment somebody tried to use it rather than
    /// when it was added. This walks the enum so that adding a sixth kind
    /// without deciding who may see it fails here instead.
    /// </remarks>
    [Fact]
    public void Every_kind_of_attachment_says_who_may_see_it()
    {
        foreach (var kind in Enum.GetValues<AttachedTo>())
        {
            var see = Docs.PermissionToSee(kind);
            var attach = Docs.PermissionToAttach(kind);

            Assert.Contains(see, Permissions.All);
            Assert.Contains(attach, Permissions.All);
        }
    }
}
