// Test fixture for semantic classification: one of each kind of name.
namespace Game
{
enum class EColor { Red, Green };

struct FPoint
{
    int X = 0;
    static int Count;
    int Length() const { return X; }
    static FPoint Origin() { return FPoint(); }
    virtual ~FPoint() = default;
    virtual void Draw() {}
};

struct FPoint3 : FPoint
{
    void Draw() override {}
};

int GlobalCounter = 0;

template <typename TValue, int Size>
TValue Twice(TValue Value)
{
    TValue Result = Value * 2 + Size;
    static int Calls = 0;
    ++Calls;
    FPoint Point;
    Point.X = Result;
    EColor Color = EColor::Green;
    (void)Color;
    GlobalCounter += Point.Length();
    return Result;
}
} // namespace Game

using Alias = Game::FPoint;
int Use() { return Game::Twice<int, 1>(2); }
