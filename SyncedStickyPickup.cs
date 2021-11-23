﻿/*
 * Copyright 2021 AlexOttr <alex@ottr.one>
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the
 * "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, 
 * distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to 
 * the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF 
 * MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY 
 * CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
 * SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Enums;

namespace OttrOne.StickyPickup
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class SyncedStickyPickup : UdonSharpBehaviour
    {
        [UdonSynced, FieldChangeCallback(nameof(SyncedBoneIndex)), HideInInspector]
        public int BoneIndex; // couters idle since its either attached to hand or body bone
        public int SyncedBoneIndex
        {
            set
            {
                localStickedOn = false;
                localPickedUp = false; // in case it gets stolen from the hands
                BoneIndex = value;
            }
            get => BoneIndex;
        }
        [Tooltip("Root bone for the Sticky Pickup.")]
        public HumanBodyBones Bone;
        [Tooltip("Set if Sticky Pickup only works for player in VR.")]
        public bool OnlyVR;
        [Tooltip("Radius of the spherical tracking area.")]
        public float Radius = 0.3f;

        private bool localStickedOn = false;
        private bool localPickedUp = false;

        private Vector3 _origPos;
        private Quaternion _origRot;

        [UdonSynced, FieldChangeCallback(nameof(PutGravity)), HideInInspector]
        public bool isKinematic;
        private bool wasKinematic;
        private bool isRested;

        public bool PutGravity
        {
            set
            {
                this.isKinematic = !value;
                this.rigidBody.isKinematic = !value;
                // when gravity is re-enabled, set rested to false on all clients
                this.isRested = false; // since the bone check catches before, this is still okay when Put = false
            }
        }

        private Rigidbody rigidBody;
        private VRC_Pickup pickup;

        [UdonSynced, HideInInspector]
        public Vector3 objectPosOffset;
        [UdonSynced, HideInInspector]
        public Quaternion objectRotOffset;

        public void Start()
        {
            this.rigidBody = (Rigidbody)gameObject.GetComponent(typeof(Rigidbody));
            this.pickup = (VRC_Pickup)gameObject.GetComponent(typeof(VRC_Pickup));
            this.wasKinematic = rigidBody.isKinematic;
            this.BoneIndex = -1;
            this.isRested = true;

            _origPos = new Vector3(rigidBody.transform.position.x, rigidBody.transform.position.y, rigidBody.transform.position.z);
            _origRot = new Quaternion(rigidBody.transform.rotation.x, rigidBody.transform.rotation.y, rigidBody.transform.rotation.z, rigidBody.transform.rotation.w);
            if (this.Radius < 0) Radius = 0f;
        }

        public void ResetPosition()
        {
            this.rigidBody.transform.SetPositionAndRotation(_origPos, _origRot);
        }

        public void ResetPositionAllPlayers()
        {
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "ResetPosition");
        }

        /// <summary>
        /// Called when the object gets picked up. It will set the owner to the perso who picked it up,
        /// set the global variables and disable the gravity effect
        /// </summary>
        public override void OnPickup()
        {
            Networking.SetOwner(Networking.LocalPlayer, this.gameObject);
            Debug.Log($"Pickup");
            // synced variables
            SyncedBoneIndex = pickup.currentHand == VRC_Pickup.PickupHand.Left ? (int)HumanBodyBones.LeftHand : (int)HumanBodyBones.RightHand;
            CalculateOffsets((HumanBodyBones)this.BoneIndex);

            localStickedOn = false;
            localPickedUp = true;

            // disable gravity effect if existing
            if (wasKinematic == false)
            {
                PutGravity = false;
            }
            RequestSerialization();
        }

        private void CalculateOffsets(HumanBodyBones bone)
        {
            Vector3 objPos = this.rigidBody.transform.position;
            Vector3 plyPos = Networking.LocalPlayer.GetBonePosition(bone);
            Quaternion invRot = Quaternion.Inverse(Networking.LocalPlayer.GetBoneRotation(bone));
            // q^(-1) * Vector (x2-x1, y2-y1, z2-z1)
            objectPosOffset = invRot * (objPos - plyPos);
            // calculate the rotation by multiplying the current rotation with inverse player rotation
            objectRotOffset = invRot * this.rigidBody.transform.rotation;
        }

        /// <summary>
        /// Drop calculates the current offset to the selected bone. If its within the radius it assumes that the player wants to place
        /// the item there so it attaches it to the player.
        /// 
        /// If its outside the radius it will put it into idle state.
        /// </summary>
        public override void OnDrop()
        {
            Vector3 objPos = this.rigidBody.transform.position;
            Vector3 plyPos = Networking.LocalPlayer.GetBonePosition(Bone);

            if (Vector3.Distance(objPos, plyPos) <= Radius && (Networking.LocalPlayer.IsUserInVR() || !OnlyVR))
            {
                SyncedBoneIndex = (int)Bone;

                localStickedOn = true;
                localPickedUp = false;
                CalculateOffsets(Bone);
            }
            else
            {
                SyncedBoneIndex = -1;
                localPickedUp = false;

                // reset gravity
                if (wasKinematic == false)
                {
                    PutGravity = true;
                }
            }
            RequestSerialization();
        }

        public override void PostLateUpdate()
        {
            if (localStickedOn)
            {
                Vector3 bonePosition = Networking.LocalPlayer.GetBonePosition(Bone);
                Quaternion boneRotation = Networking.LocalPlayer.GetBoneRotation(Bone);

                // calculate the wanted offset by multiplying the bonerotation with the position offset and add this to the targeted bone position
                // calculate the wanted rotation by multiplying the current bone rotation with the calculated rotation offset 
                this.rigidBody.transform.SetPositionAndRotation((boneRotation * objectPosOffset) + bonePosition, boneRotation * objectRotOffset);
            }
            else if (localPickedUp)
            {
                CalculateOffsets((HumanBodyBones)this.BoneIndex);
                RequestSerialization();
            }
            else if (this.BoneIndex >= 0)
            {
                Vector3 bonePosition = Networking.GetOwner(gameObject).GetBonePosition((HumanBodyBones)this.BoneIndex);
                Quaternion boneRotation = Networking.GetOwner(gameObject).GetBoneRotation((HumanBodyBones)this.BoneIndex);

                this.rigidBody.transform.SetPositionAndRotation((boneRotation * objectPosOffset) + bonePosition, boneRotation * objectRotOffset);
            }
            else if (!isRested) // loop part gets called when object is still moving while not being attached to something
            {
                // idle gravity mode -> sync position till next pickup
                if (objectPosOffset != rigidBody.transform.position)
                {
                    if (Networking.GetOwner(gameObject) == Networking.LocalPlayer)
                    {
                        this.objectPosOffset = rigidBody.transform.position;
                        this.objectRotOffset = rigidBody.transform.rotation;
                        RequestSerialization();
                    }
                    else
                    {
                        rigidBody.transform.SetPositionAndRotation(objectPosOffset, objectRotOffset);
                    }
                }
                else
                {
                    isRested = true;
                }
            }
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            SendCustomEventDelayedSeconds("SyncLatePlayer", 2.5F, EventTiming.Update);
        }

        public void SyncLatePlayer()
        {
            RequestSerialization();
        }
    }
}