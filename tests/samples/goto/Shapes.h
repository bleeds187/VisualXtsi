// Test fixture for Go to Definition.
#pragma once

#define SHAPE_SIZE 3

class Shape
{
public:
    void Draw();
    int Area() const { return Size * Size; }
    int Size = SHAPE_SIZE;
};

class Canvas
{
public:
    void Paint(); // declared only: no definition anywhere
};

class Brush
{
public:
    void Paint();
};
