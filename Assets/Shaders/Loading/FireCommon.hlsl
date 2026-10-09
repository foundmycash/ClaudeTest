#ifndef LOADING_FIRE_COMMON_INCLUDED
#define LOADING_FIRE_COMMON_INCLUDED

// Colour of fire by temperature, shared by the fireball, the full-screen fire and the loading UI's burn edge so all
// three read as the same flames. The project renders in linear colour space, so these are linear values (the sRGB
// colours they come from are in the comments); sRGB values here would come out pale and washed out.
//   heat <= 0: smoke   0.25: deep red   0.5: orange   0.75: yellow   1: white-hot   > 1: brighter than white (HDR)
half3 FireRamp(float heat)
{
    const half3 smoke  = half3(0.012, 0.010, 0.009);   // sRGB 0.12, 0.10, 0.09
    const half3 red    = half3(0.30, 0.012, 0.002);    // sRGB 0.58, 0.11, 0.04
    const half3 orange = half3(1.00, 0.16, 0.008);     // sRGB 1.00, 0.44, 0.08
    const half3 yellow = half3(1.00, 0.55, 0.07);      // sRGB 1.00, 0.77, 0.29
    const half3 white  = half3(1.00, 0.86, 0.62);      // sRGB 1.00, 0.94, 0.81

    float h = saturate(heat) * 4.0;
    half3 c = lerp(smoke, red, saturate(h));
    c = lerp(c, orange, saturate(h - 1.0));
    c = lerp(c, yellow, saturate(h - 2.0));
    c = lerp(c, white, saturate(h - 3.0));
    return c * (1.0 + max(heat - 1.0, 0.0) * 2.0);
}

#endif
