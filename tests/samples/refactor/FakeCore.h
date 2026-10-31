// Test fixture: just enough of an Unreal-like core for the refactoring queries to parse.
#pragma once

#define UCLASS(...)
#define USTRUCT(...)
#define UFUNCTION(...)
#define UPROPERTY(...)
#define UPARAM(...)
#define GENERATED_BODY()
#define FORCEINLINE inline
#define GADGETS_API
#define TEXT(x) L##x

typedef int int32;

class FString
{
public:
    FString() {}
    FString(const wchar_t*) {}
};

template <typename T>
class TArray
{
public:
    int Num() const { return 0; }
};

class UObject
{
public:
    virtual ~UObject() {}
    virtual int Score() const { return 0; }
};