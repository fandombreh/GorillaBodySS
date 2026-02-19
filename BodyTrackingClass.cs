using UnityEngine;

namespace GorillaBodyServer
{
    /// <summary>
    /// Holds synced body tracking data for a player.
    /// Chest, left elbow, and right elbow positions/rotations.
    /// </summary>
    public class BodyTrackingData
    {
        public Vector3 ChestPosition;
        public Quaternion ChestRotation;

        public Vector3 LeftElbowPosition;
        public Quaternion LeftElbowRotation;

        public Vector3 RightElbowPosition;
        public Quaternion RightElbowRotation;

        // Pack into object array for Photon RPC
        public object[] ToRPCData()
        {
            return new object[]
            {
                ChestPosition.x, ChestPosition.y, ChestPosition.z,
                ChestRotation.x, ChestRotation.y, ChestRotation.z, ChestRotation.w,

                LeftElbowPosition.x, LeftElbowPosition.y, LeftElbowPosition.z,
                LeftElbowRotation.x, LeftElbowRotation.y, LeftElbowRotation.z, LeftElbowRotation.w,

                RightElbowPosition.x, RightElbowPosition.y, RightElbowPosition.z,
                RightElbowRotation.x, RightElbowRotation.y, RightElbowRotation.z, RightElbowRotation.w
            };
        }

        public static BodyTrackingData FromRPCData(object[] data)
        {
            if (data == null || data.Length < 21) return null;

            int i = 0;
            return new BodyTrackingData
            {
                ChestPosition = new Vector3((float)data[i++], (float)data[i++], (float)data[i++]),
                ChestRotation = new Quaternion((float)data[i++], (float)data[i++], (float)data[i++], (float)data[i++]),

                LeftElbowPosition = new Vector3((float)data[i++], (float)data[i++], (float)data[i++]),
                LeftElbowRotation = new Quaternion((float)data[i++], (float)data[i++], (float)data[i++], (float)data[i++]),

                RightElbowPosition = new Vector3((float)data[i++], (float)data[i++], (float)data[i++]),
                RightElbowRotation = new Quaternion((float)data[i++], (float)data[i++], (float)data[i++], (float)data[i++])
            };
        }
    }
}
