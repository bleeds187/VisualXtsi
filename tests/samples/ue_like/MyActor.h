#pragma once
#include "FakeUObject/ObjectMacros.h"
#include "MyActor.generated.h"

// Test fixture: Unreal-style markup on a plain class. Not Unreal code.
class ABaseActor
{
};
UCLASS(BlueprintType)
class AMyActor : public ABaseActor
{
    GENERATED_BODY()

public:
    UPROPERTY(EditAnywhere)
    int Health = 100;
};
