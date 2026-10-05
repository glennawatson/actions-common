// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

namespace ActionsCommon.Signing;

/// <summary>Stores a window's screen position and size.</summary>
/// <param name="Id">The window identifier.</param>
/// <param name="X">The horizontal screen position.</param>
/// <param name="Y">The vertical screen position.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
internal readonly record struct Window(string Id, int X, int Y, int Width, int Height)
{
    /// <summary>Gets the window's area in square pixels.</summary>
    public int Area => Width * Height;
}
