using GitDotNet.Indexing.LiteDb.Data;
using GitDotNet.Indexing.Realm;
using LangChain.Databases.Sqlite;
using LangChain.Providers.Ollama;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LangChain.Databases;
using GitDotNet.Indexing.LiteDb.Converters;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal;

namespace GitDotNet.Indexing.LiteDb;

internal class SqliteDatabaseContext : DbContext
{
    public DbSet<IndexedBlob> IndexedBlobs { get; set; }
    public DbSet<CommitContent> Commits { get; set; }
    public SqLiteVectorDatabase? VectorDatabase { get; private set; }
    //public OllamaEmbeddingModel? EmbeddingModel { get; private set; }
    //public IVectorCollection? VectorCollection { get; private set; }

    public string Path { get; }
    public IOptions<GitIndexing.Options> Options { get; }

    internal SqliteDatabaseContext(string path, IOptions<GitIndexing.Options> options)
    {
        Path = path;
        Options = options;

        Database.EnsureCreated();
    }

    public SqliteDatabaseContext() : this(
        @".\Database\data.db",
        Microsoft.Extensions.Options.Options.Create<GitIndexing.Options>(new()
        {
            IndexProviders = [], IndexTypes = []
        }))
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        VectorDatabase = new SqLiteVectorDatabase(dataSource: Path);
        var connection = (SqliteConnection)typeof(SqLiteVectorDatabase)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(VectorDatabase)!;
        optionsBuilder.UseSqlite(connection);

        optionsBuilder
            .ReplaceService<IMigrationsSqlGenerator, MigrationsSqlGenerator>();
        //var provider = new OllamaProvider();
        //EmbeddingModel = new OllamaEmbeddingModel(provider, id: "all-minilm");
        //var llm = new OllamaChatModel(provider, id: "llama3");
        //VectorCollection = AsyncHelper.RunSync(() => VectorDatabase!.GetOrCreateCollectionAsync(collectionName: "vectors", dimensions: 1536));
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder
            .Properties<HashId>()
            .HaveConversion<HashIdConversion, HashIdComparer>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CommitContent>()
            .Property(x => x.Blobs)
            .HasConversion<HashIdDictionaryConversion>()
            .IsConcurrencyToken(false);
        modelBuilder.Entity<CommitContent>()
            .Property(x => x.Id);
        foreach (var type in Options.Value.IndexTypes)
        {
            modelBuilder.Entity(type);
        }
    }
}