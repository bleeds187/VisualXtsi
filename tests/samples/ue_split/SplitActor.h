#pragma once
#include "MiniMacros.h"
UCLASS()
class ASplitActor
{
    GENERATED_BODY()
public:
    UFUNCTION(BlueprintCallable, BlueprintNativeEvent, Category="Split")
    void PerformTravel();
    UFUNCTION(Server, Reliable, WithValidation)
    void ServerSetScore(int Score);
    UFUNCTION(Client, Unreliable)
    void ClientNotify();
    UFUNCTION(NetMulticast, Reliable)
    void MulticastBoom();
    void Plain();
    void CallAll();
};
