#include "SplitActor.h"

void ASplitActor::PerformTravel_Implementation() {}
bool ASplitActor::ServerSetScore_Validate(int Score) { return Score >= 0; }
void ASplitActor::ServerSetScore_Implementation(int Score) {}
void ASplitActor::ClientNotify_Implementation() {}
void ASplitActor::MulticastBoom_Implementation() {}
void ASplitActor::Plain() {}
void ASplitActor::CallAll()
{
    PerformTravel();
    ServerSetScore(1);
}
