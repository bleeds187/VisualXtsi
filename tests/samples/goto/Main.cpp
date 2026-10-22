#include "Shapes.h"

int UseShape()
{
    Shape s;
    s.Draw();
    return s.Area() + SHAPE_SIZE;
}

void UseCanvas()
{
    Canvas c;
    c.Paint();
}
