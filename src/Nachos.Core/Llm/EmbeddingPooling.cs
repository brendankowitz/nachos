namespace Nachos.Core.Llm;

/// <summary>How a sentence-embedding model turns its per-token hidden states into one vector.</summary>
public enum EmbeddingPooling
{
    /// <summary>The hidden state of the first token (<c>[CLS]</c>).</summary>
    Cls,

    /// <summary>The average of the hidden states of the tokens the attention mask keeps.</summary>
    Mean,
}
