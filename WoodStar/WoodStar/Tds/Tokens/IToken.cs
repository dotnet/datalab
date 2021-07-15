namespace WoodStar;

public interface IToken
{
    TokenType TokenType { get; }

    int TokenLength { get; }
}
