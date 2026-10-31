// Test fixture: a class with the function shapes the refactorings handle.
#pragma once

#include "FakeCore.h"

namespace Toys
{
struct FPart
{
    int Id = 0;
};
}

UCLASS()
class GADGETS_API UGadget : public UObject
{
    GENERATED_BODY()
public:
    struct FSettings
    {
        float Speed = 1.f;
    };

    UGadget();
    virtual ~UGadget();
    UFUNCTION(BlueprintCallable)
    virtual FSettings GetSettings(int32 Index, const FString& Name = TEXT("x"), bool bLoud = false) const;
    static UGadget* Make(UPARAM(ref) int32& Count);
    UFUNCTION(BlueprintNativeEvent)
    void OnPoked(int32 Strength);
    UFUNCTION(Server, Reliable, WithValidation)
    void ServerPush(float Force);
    FORCEINLINE int32 GetCount() const { return Count; }
    Toys::FPart MakePart() noexcept;
    void Defined();
    virtual int Score() const override;
    UFUNCTION(BlueprintImplementableEvent)
    void OnShown();

    UPROPERTY(EditAnywhere)
    int32 Count = 0;
    TArray<FString> Names;
};

enum class EGadgetMode : unsigned char
{
    Off,
    On,
};