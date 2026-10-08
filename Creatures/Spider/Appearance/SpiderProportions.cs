namespace WebCrawler;

/// <summary>
/// The spider's body plan. Its behaviour uses these to place feet, and its appearance uses
/// them to draw legs, so changing a number here changes both how it looks and how it walks.
/// </summary>
static class SpiderProportions
{
    // Leg angle from straight ahead, front pair to back pair (radians; mirrored for the other side).
    public static readonly float[] LegAngles = { 0.6f, 1.2f, 1.95f, 2.5f };
    // How far each foot rests from the body: front and back pairs longest.
    public static readonly float[] LegReach = { 52f, 44f, 38f, 48f };
    // Where each leg joins the body, measured forward from its centre.
    public static readonly float[] HipAlong = { 7f, 5f, 3f, 1f };
    // Sideways offset of the hips from the midline.
    public const float HipSide = 3.5f;

    // Segment lengths as a share of the leg's reach: femur, tibia, metatarsus.
    public const float Femur = 0.55f, Tibia = 0.55f, Metatarsus = 0.25f;
    // The metatarsus rises from the foot at about 70 degrees.
    public const float AnkleAngle = 1.2f;

    // How high the body rides above the ground.
    public const float BodyHeight = 13f;
    // Fake 3D: height shifts a point up the screen, like a camera tilted slightly forward.
    public const float Tilt = 0.5f;

    // Body box size and where its head node and mouth sit.
    public const float BodyLength = 18f, BodyWidth = 9f;
    public const float HeadAhead = 12f, MouthAhead = 14f;
}
