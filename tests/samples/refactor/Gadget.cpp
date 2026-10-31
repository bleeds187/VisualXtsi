// Test fixture: some of UGadget's definitions, and a free function.
#include "Gadget.h"

UGadget::UGadget()
{
}

UGadget::FSettings UGadget::GetSettings(int32 Index, const FString& Name, bool bLoud) const
{
    return FSettings();
}

void UGadget::Defined()
{
    Count = 1;
}

void FreeHelper(int A, int B = 2)
{
}

int Braces(int X)
{
    if (X > 0)
    {
        X = 1;
    }
    else
        X = 2;
    for (int I = 0; I < 3; ++I)
        X += I;
    return X;
}
