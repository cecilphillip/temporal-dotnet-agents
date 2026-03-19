using FakeItEasy;
using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableEmbeddingGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_PassesThroughWhenNotInWorkflow()
    {
        var expectedEmbeddings = new GeneratedEmbeddings<Embedding<float>>([
            new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })
        ]);

        var innerGenerator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        A.CallTo(() => innerGenerator.GenerateAsync(
                A<IEnumerable<string>>._, A<EmbeddingGenerationOptions?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(expectedEmbeddings));

        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var generator = new DurableEmbeddingGenerator(innerGenerator, options);

        var result = await generator.GenerateAsync(["Hello world"]);

        Assert.Same(expectedEmbeddings, result);
        A.CallTo(() => innerGenerator.GenerateAsync(
                A<IEnumerable<string>>._, A<EmbeddingGenerationOptions?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        var innerGenerator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        Assert.Throws<ArgumentNullException>(
            () => new DurableEmbeddingGenerator(innerGenerator, null!));
    }

    [Fact]
    public void GetService_ReturnsDurableExecutionOptions()
    {
        var innerGenerator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var generator = new DurableEmbeddingGenerator(innerGenerator, options);

        var result = generator.GetService<DurableExecutionOptions>();
        Assert.Same(options, result);
    }

    [Fact]
    public void UseDurableExecution_ThrowsOnNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(
            () => EmbeddingGeneratorBuilderExtensions.UseDurableExecution(null!));
    }

    [Fact]
    public void UseDurableExecution_CreatesPipeline()
    {
        var innerGenerator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        var builder = new EmbeddingGeneratorBuilder<string, Embedding<float>>(innerGenerator);

        builder.UseDurableExecution(opts => opts.TaskQueue = "emb-queue");
        var pipeline = builder.Build();

        var durableOptions = pipeline.GetService<DurableExecutionOptions>();
        Assert.NotNull(durableOptions);
        Assert.Equal("emb-queue", durableOptions!.TaskQueue);
    }

    [Fact]
    public void DurableEmbeddingActivities_Constructor_AcceptsNullLogger()
    {
        var generator = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        var activities = new DurableEmbeddingActivities(generator, null);
        Assert.NotNull(activities);
    }
}
