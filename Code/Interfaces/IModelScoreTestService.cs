namespace TinyGenerator.Services;

public interface IModelScoreTestService
{
    string ScoreOperation { get; }
    CommandHandle EnqueueForMissingModels();
    CommandHandle EnqueueForModel(string modelName);
    CommandHandle? EnqueueForModel(int modelId);
}

