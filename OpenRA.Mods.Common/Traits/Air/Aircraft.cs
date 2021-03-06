#region Copyright & License Information
/*
 * Copyright 2007-2017 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Orders;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public class AircraftInfo : ITraitInfo, IPositionableInfo, IFacingInfo, IMoveInfo, ICruiseAltitudeInfo,
		UsesInit<LocationInit>, UsesInit<FacingInit>, IActorPreviewInitInfo
	{
		public readonly WDist CruiseAltitude = new WDist(1280);
		public readonly WDist IdealSeparation = new WDist(1706);
		[Desc("Whether the aircraft can be repulsed.")]
		public readonly bool Repulsable = true;
		[Desc("The speed at which the aircraft is repulsed from other aircraft. Specify -1 for normal movement speed.")]
		public readonly int RepulsionSpeed = -1;

		[ActorReference]
		public readonly HashSet<string> RepairBuildings = new HashSet<string> { };
		[ActorReference]
		public readonly HashSet<string> RearmBuildings = new HashSet<string> { };
		public readonly int InitialFacing = 0;
		public readonly int TurnSpeed = 255;
		public readonly int Speed = 1;

		[Desc("Minimum altitude where this aircraft is considered airborne")]
		public readonly int MinAirborneAltitude = 1;
		public readonly HashSet<string> LandableTerrainTypes = new HashSet<string>();

		[Desc("Can the actor be ordered to move in to shroud?")]
		public readonly bool MoveIntoShroud = true;

		public virtual object Create(ActorInitializer init) { return new Aircraft(init, this); }
		public int GetInitialFacing() { return InitialFacing; }
		public WDist GetCruiseAltitude() { return CruiseAltitude; }

		[VoiceReference] public readonly string Voice = "Action";

		[GrantedConditionReference]
		[Desc("The condition to grant to self while airborne.")]
		public readonly string AirborneCondition = null;

		[GrantedConditionReference]
		[Desc("The condition to grant to self while at cruise altitude.")]
		public readonly string CruisingCondition = null;

		[Desc("Can the actor hover in place mid-air? If not, then the actor will have to remain in motion (circle around).")]
		public readonly bool CanHover = false;

		[Desc("Will this actor try to land after it has no more commands?")]
		public readonly bool LandWhenIdle = true;

		[Desc("Does this actor need to turn before landing?")]
		public readonly bool TurnToLand = false;

		[Desc("Does this actor cancel its previous activity after resupplying?")]
		public readonly bool AbortOnResupply = true;

		public readonly WDist LandAltitude = WDist.Zero;

		[Desc("How fast this actor ascends or descends when using horizontal take off/landing.")]
		public readonly WAngle MaximumPitch = WAngle.FromDegrees(10);

		[Desc("How fast this actor ascends or descends when using vertical take off/landing.")]
		public readonly WDist AltitudeVelocity = new WDist(43);

		[Desc("Sound to play when the actor is taking off.")]
		public readonly string TakeoffSound = null;

		[Desc("Sound to play when the actor is landing.")]
		public readonly string LandingSound = null;

		[Desc("The distance of the resupply base that the aircraft will wait for its turn.")]
		public readonly WDist WaitDistanceFromResupplyBase = new WDist(3072);

		[Desc("The number of ticks that a airplane will wait to make a new search for an available airport.")]
		public readonly int NumberOfTicksToVerifyAvailableAirport = 150;

		[Desc("Facing to use for actor previews (map editor, color picker, etc)")]
		public readonly int PreviewFacing = 92;

		IEnumerable<object> IActorPreviewInitInfo.ActorPreviewInits(ActorInfo ai, ActorPreviewType type)
		{
			yield return new FacingInit(PreviewFacing);
		}

		public IReadOnlyDictionary<CPos, SubCell> OccupiedCells(ActorInfo info, CPos location, SubCell subCell = SubCell.Any) { return new ReadOnlyDictionary<CPos, SubCell>(); }
		bool IOccupySpaceInfo.SharesCell { get { return false; } }

		// Used to determine if an aircraft can spawn landed
		public bool CanEnterCell(World world, Actor self, CPos cell, Actor ignoreActor = null, bool checkTransientActors = true)
		{
			if (!world.Map.Contains(cell))
				return false;

			var type = world.Map.GetTerrainInfo(cell).Type;
			if (!LandableTerrainTypes.Contains(type))
				return false;

			if (world.WorldActor.Trait<BuildingInfluence>().GetBuildingAt(cell) != null)
				return false;

			if (!checkTransientActors)
				return true;

			return !world.ActorMap.GetActorsAt(cell).Any(x => x != ignoreActor);
		}
	}

	public class Aircraft : ITick, ISync, IFacing, IPositionable, IMove, IIssueOrder, IResolveOrder, IOrderVoice, IDeathActorInitModifier,
		INotifyCreated, INotifyAddedToWorld, INotifyRemovedFromWorld, INotifyActorDisposing, IActorPreviewInitModifier, IIssueDeployOrder
	{
		static readonly Pair<CPos, SubCell>[] NoCells = { };

		public readonly bool IsPlane;
		public readonly AircraftInfo Info;
		readonly Actor self;

		ConditionManager conditionManager;
		IDisposable reservation;
		IEnumerable<int> speedModifiers;

		[Sync] public int Facing { get; set; }
		[Sync] public WPos CenterPosition { get; private set; }
		public CPos TopLeft { get { return self.World.Map.CellContaining(CenterPosition); } }
		public int TurnSpeed { get { return Info.TurnSpeed; } }
		public Actor ReservedActor { get; private set; }
		public bool MayYieldReservation { get; private set; }

		bool airborne;
		bool cruising;
		bool firstTick = true;
		int airborneToken = ConditionManager.InvalidConditionToken;
		int cruisingToken = ConditionManager.InvalidConditionToken;

		bool isMoving;
		bool isMovingVertically;
		WPos cachedPosition;

		public Aircraft(ActorInitializer init, AircraftInfo info)
		{
			Info = info;
			self = init.Self;

			if (init.Contains<LocationInit>())
				SetPosition(self, init.Get<LocationInit, CPos>());

			if (init.Contains<CenterPositionInit>())
				SetPosition(self, init.Get<CenterPositionInit, WPos>());

			Facing = init.Contains<FacingInit>() ? init.Get<FacingInit, int>() : info.InitialFacing;

			// TODO: HACK: This is a hack until we can properly distinguish between airplane and helicopter!
			// Or until the activities get unified enough so that it doesn't matter.
			IsPlane = !info.CanHover;
		}

		public void Created(Actor self)
		{
			conditionManager = self.TraitOrDefault<ConditionManager>();
			speedModifiers = self.TraitsImplementing<ISpeedModifier>().ToArray().Select(sm => sm.GetSpeedModifier());
			cachedPosition = self.CenterPosition;
		}

		public void AddedToWorld(Actor self)
		{
			self.World.AddToMaps(self, this);

			var altitude = self.World.Map.DistanceAboveTerrain(CenterPosition);
			if (altitude.Length >= Info.MinAirborneAltitude)
				OnAirborneAltitudeReached();
			if (altitude == Info.CruiseAltitude)
				OnCruisingAltitudeReached();
		}

		public virtual void Tick(Actor self)
		{
			if (firstTick)
			{
				firstTick = false;

				// TODO: Aircraft husks don't properly unreserve.
				if (self.Info.HasTraitInfo<FallsToEarthInfo>())
					return;

				ReserveSpawnBuilding();

				var host = GetActorBelow();
				if (host == null)
					return;

				self.QueueActivity(new TakeOff(self));
			}

			var oldCachedPosition = cachedPosition;
			cachedPosition = self.CenterPosition;
			isMoving = (oldCachedPosition - cachedPosition).HorizontalLengthSquared != 0;
			isMovingVertically = (oldCachedPosition - cachedPosition).VerticalLengthSquared != 0;

			Repulse();
		}

		public void Repulse()
		{
			var repulsionForce = GetRepulsionForce();
			if (repulsionForce.HorizontalLengthSquared == 0)
				return;

			var speed = Info.RepulsionSpeed != -1 ? Info.RepulsionSpeed : MovementSpeed;
			SetPosition(self, CenterPosition + FlyStep(speed, repulsionForce.Yaw.Facing));
		}

		public virtual WVec GetRepulsionForce()
		{
			if (!Info.Repulsable)
				return WVec.Zero;

			if (reservation != null)
			{
				var distanceFromReservationActor = (ReservedActor.CenterPosition - self.CenterPosition).HorizontalLength;
				if (distanceFromReservationActor < Info.WaitDistanceFromResupplyBase.Length)
					return WVec.Zero;
			}

			// Repulsion only applies when we're flying!
			var altitude = self.World.Map.DistanceAboveTerrain(CenterPosition).Length;
			if (altitude != Info.CruiseAltitude.Length)
				return WVec.Zero;

			// PERF: Avoid LINQ.
			var repulsionForce = WVec.Zero;
			foreach (var actor in self.World.FindActorsInCircle(self.CenterPosition, Info.IdealSeparation))
			{
				if (actor.IsDead)
					continue;

				var ai = actor.Info.TraitInfoOrDefault<AircraftInfo>();
				if (ai == null || !ai.Repulsable || ai.CruiseAltitude != Info.CruiseAltitude)
					continue;

				repulsionForce += GetRepulsionForce(actor);
			}

			// Actors outside the map bounds receive an extra nudge towards the center of the map
			if (!self.World.Map.Contains(self.Location))
			{
				// The map bounds are in projected coordinates, which is technically wrong for this,
				// but we avoid the issues in practice by guessing the middle of the map instead of the edge
				var center = WPos.Lerp(self.World.Map.ProjectedTopLeft, self.World.Map.ProjectedBottomRight, 1, 2);
				repulsionForce += new WVec(1024, 0, 0).Rotate(WRot.FromYaw((self.CenterPosition - center).Yaw));
			}

			if (Info.CanHover)
				return repulsionForce;

			// Non-hovering actors mush always keep moving forward, so they need extra calculations.
			var currentDir = FlyStep(Facing);
			var length = currentDir.HorizontalLength * repulsionForce.HorizontalLength;
			if (length == 0)
				return WVec.Zero;

			var dot = WVec.Dot(currentDir, repulsionForce) / length;

			// avoid stalling the plane
			return dot >= 0 ? repulsionForce : WVec.Zero;
		}

		public WVec GetRepulsionForce(Actor other)
		{
			if (self == other || other.CenterPosition.Z < self.CenterPosition.Z)
				return WVec.Zero;

			var d = self.CenterPosition - other.CenterPosition;
			var distSq = d.HorizontalLengthSquared;
			if (distSq > Info.IdealSeparation.LengthSquared)
				return WVec.Zero;

			if (distSq < 1)
			{
				var yaw = self.World.SharedRandom.Next(0, 1023);
				var rot = new WRot(WAngle.Zero, WAngle.Zero, new WAngle(yaw));
				return new WVec(1024, 0, 0).Rotate(rot);
			}

			return (d * 1024 * 8) / (int)distSq;
		}

		public Actor GetActorBelow()
		{
			// Map.DistanceAboveTerrain(WPos pos) is called directly because Aircraft is an IPositionable trait
			// and all calls occur in Tick methods.
			if (self.World.Map.DistanceAboveTerrain(CenterPosition).Length != 0)
				return null; // not on the ground.

			return self.World.ActorMap.GetActorsAt(self.Location)
				.FirstOrDefault(a => a.Info.HasTraitInfo<ReservableInfo>());
		}

		protected void ReserveSpawnBuilding()
		{
			// HACK: Not spawning in the air, so try to associate with our spawner.
			var spawner = GetActorBelow();
			if (spawner == null)
				return;

			MakeReservation(spawner);
		}

		public void MakeReservation(Actor target)
		{
			UnReserve();
			var reservable = target.TraitOrDefault<Reservable>();
			if (reservable != null)
			{
				reservation = reservable.Reserve(target, self, this);
				ReservedActor = target;
			}
		}

		public void AllowYieldingReservation()
		{
			if (reservation == null)
				return;

			MayYieldReservation = true;
		}

		public void UnReserve()
		{
			if (reservation == null)
				return;

			reservation.Dispose();
			reservation = null;
			ReservedActor = null;
			MayYieldReservation = false;

			if (self.World.Map.DistanceAboveTerrain(CenterPosition).Length <= Info.LandAltitude.Length)
				self.QueueActivity(new TakeOff(self));
		}

		public bool AircraftCanEnter(Actor a)
		{
			if (self.AppearsHostileTo(a))
				return false;

			return Info.RearmBuildings.Contains(a.Info.Name)
				|| Info.RepairBuildings.Contains(a.Info.Name);
		}

		public int MovementSpeed
		{
			get { return Util.ApplyPercentageModifiers(Info.Speed, speedModifiers); }
		}

		public IEnumerable<Pair<CPos, SubCell>> OccupiedCells() { return NoCells; }

		public WVec FlyStep(int facing)
		{
			return FlyStep(MovementSpeed, facing);
		}

		public WVec FlyStep(int speed, int facing)
		{
			var dir = new WVec(0, -1024, 0).Rotate(WRot.FromFacing(facing));
			return speed * dir / 1024;
		}

		public bool CanLand(CPos cell)
		{
			if (!self.World.Map.Contains(cell))
				return false;

			if (self.World.ActorMap.AnyActorsAt(cell))
				return false;

			var type = self.World.Map.GetTerrainInfo(cell).Type;
			return Info.LandableTerrainTypes.Contains(type);
		}

		public virtual IEnumerable<Activity> GetResupplyActivities(Actor a)
		{
			var name = a.Info.Name;
			if (Info.RearmBuildings.Contains(name))
				yield return new Rearm(self);

			// The ResupplyAircraft activity guarantees that we're on the helipad
			if (Info.RepairBuildings.Contains(name))
				yield return new Repair(self, a, WDist.Zero);
		}

		public void ModifyDeathActorInit(Actor self, TypeDictionary init)
		{
			init.Add(new FacingInit(Facing));
		}

		#region Implement IPositionable

		public bool IsLeavingCell(CPos location, SubCell subCell = SubCell.Any) { return false; } // TODO: Handle landing
		public bool CanEnterCell(CPos cell, Actor ignoreActor = null, bool checkTransientActors = true) { return true; }
		public SubCell GetValidSubCell(SubCell preferred) { return SubCell.Invalid; }
		public SubCell GetAvailableSubCell(CPos a, SubCell preferredSubCell = SubCell.Any, Actor ignoreActor = null, bool checkTransientActors = true)
		{
			// Does not use any subcell
			return SubCell.Invalid;
		}

		public void SetVisualPosition(Actor self, WPos pos) { SetPosition(self, pos); }

		// Changes position, but not altitude
		public void SetPosition(Actor self, CPos cell, SubCell subCell = SubCell.Any)
		{
			SetPosition(self, self.World.Map.CenterOfCell(cell) + new WVec(0, 0, CenterPosition.Z));
		}

		public void SetPosition(Actor self, WPos pos)
		{
			CenterPosition = pos;

			if (!self.IsInWorld)
				return;

			self.World.UpdateMaps(self, this);

			var altitude = self.World.Map.DistanceAboveTerrain(CenterPosition);
			var isAirborne = altitude.Length >= Info.MinAirborneAltitude;
			if (isAirborne && !airborne)
				OnAirborneAltitudeReached();
			else if (!isAirborne && airborne)
				OnAirborneAltitudeLeft();
			var isCruising = altitude == Info.CruiseAltitude;
			if (isCruising && !cruising)
				OnCruisingAltitudeReached();
			else if (!isCruising && cruising)
				OnCruisingAltitudeLeft();
		}

		#endregion

		#region Implement IMove

		public Activity MoveTo(CPos cell, int nearEnough)
		{
			if (IsPlane)
				return new FlyAndContinueWithCirclesWhenIdle(self, Target.FromCell(self.World, cell));

			return new HeliFly(self, Target.FromCell(self.World, cell));
		}

		public Activity MoveTo(CPos cell, Actor ignoreActor)
		{
			if (IsPlane)
				return new FlyAndContinueWithCirclesWhenIdle(self, Target.FromCell(self.World, cell));

			return new HeliFly(self, Target.FromCell(self.World, cell));
		}

		public Activity MoveWithinRange(Target target, WDist range)
		{
			if (IsPlane)
				return new FlyAndContinueWithCirclesWhenIdle(self, target, WDist.Zero, range);

			return new HeliFly(self, target, WDist.Zero, range);
		}

		public Activity MoveWithinRange(Target target, WDist minRange, WDist maxRange)
		{
			if (IsPlane)
				return new FlyAndContinueWithCirclesWhenIdle(self, target, minRange, maxRange);

			return new HeliFly(self, target, minRange, maxRange);
		}

		public Activity MoveFollow(Actor self, Target target, WDist minRange, WDist maxRange)
		{
			if (IsPlane)
				return new FlyFollow(self, target, minRange, maxRange);

			return new Follow(self, target, minRange, maxRange);
		}

		public Activity MoveIntoWorld(Actor self, CPos cell, SubCell subCell = SubCell.Any)
		{
			if (IsPlane)
				return new Fly(self, Target.FromCell(self.World, cell));

			return new HeliFly(self, Target.FromCell(self.World, cell, subCell));
		}

		public Activity MoveToTarget(Actor self, Target target)
		{
			if (IsPlane)
				return new Fly(self, target, WDist.FromCells(3), WDist.FromCells(5));

			return ActivityUtils.SequenceActivities(new HeliFly(self, target), new Turn(self, Info.InitialFacing));
		}

		public Activity MoveIntoTarget(Actor self, Target target)
		{
			if (IsPlane)
				return new Land(self, target);

			return new HeliLand(self, false);
		}

		public Activity VisualMove(Actor self, WPos fromPos, WPos toPos)
		{
			// TODO: Ignore repulsion when moving
			if (IsPlane)
				return ActivityUtils.SequenceActivities(
					new CallFunc(() => SetVisualPosition(self, fromPos)),
					new Fly(self, Target.FromPos(toPos)));

			return ActivityUtils.SequenceActivities(new CallFunc(() => SetVisualPosition(self, fromPos)),
				new HeliFly(self, Target.FromPos(toPos)));
		}

		public CPos NearestMoveableCell(CPos cell) { return cell; }

		public bool IsMoving { get { return isMoving; } set { } }

		public bool IsMovingVertically { get { return isMovingVertically; } set { } }

		public bool CanEnterTargetNow(Actor self, Target target)
		{
			if (target.Positions.Any(p => self.World.ActorMap.GetActorsAt(self.World.Map.CellContaining(p)).Any(a => a != self && a != target.Actor)))
				return false;

			MakeReservation(target.Actor);
			return true;
		}

		#endregion

		#region Implement order interfaces

		public IEnumerable<IOrderTargeter> Orders
		{
			get
			{
				yield return new EnterAlliedActorTargeter<BuildingInfo>("Enter", 5,
					target => AircraftCanEnter(target), target => !Reservable.IsReserved(target));

				yield return new AircraftMoveOrderTargeter(Info);
			}
		}

		public Order IssueOrder(Actor self, IOrderTargeter order, Target target, bool queued)
		{
			if (order.OrderID == "Enter")
				return new Order(order.OrderID, self, queued) { TargetActor = target.Actor };

			if (order.OrderID == "Move")
				return new Order(order.OrderID, self, queued) { TargetLocation = self.World.Map.CellContaining(target.CenterPosition) };

			return null;
		}

		Order IIssueDeployOrder.IssueDeployOrder(Actor self)
		{
			return new Order("ReturnToBase", self, false);
		}

		public string VoicePhraseForOrder(Actor self, Order order)
		{
			switch (order.OrderString)
			{
				case "Move":
				case "Enter":
				case "ReturnToBase":
				case "Stop":
					return Info.Voice;
				default: return null;
			}
		}

		public void ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString == "Move")
			{
				var cell = self.World.Map.Clamp(order.TargetLocation);

				if (!Info.MoveIntoShroud && !self.Owner.Shroud.IsExplored(cell))
					return;

				if (!order.Queued)
					UnReserve();

				var target = Target.FromCell(self.World, cell);

				self.SetTargetLine(target, Color.Green);

				if (IsPlane)
					self.QueueActivity(order.Queued, new FlyAndContinueWithCirclesWhenIdle(self, target));
				else
					self.QueueActivity(order.Queued, new HeliFlyAndLandWhenIdle(self, target, Info));
			}
			else if (order.OrderString == "Enter")
			{
				if (!order.Queued)
					UnReserve();

				if (Reservable.IsReserved(order.TargetActor))
				{
					if (IsPlane)
						self.QueueActivity(new ReturnToBase(self, Info.AbortOnResupply));
					else
						self.QueueActivity(new HeliReturnToBase(self, Info.AbortOnResupply));
				}
				else
				{
					self.SetTargetLine(Target.FromActor(order.TargetActor), Color.Green);

					if (IsPlane)
					{
						self.QueueActivity(order.Queued, ActivityUtils.SequenceActivities(
							new ReturnToBase(self, Info.AbortOnResupply, order.TargetActor),
							new ResupplyAircraft(self)));
					}
					else
					{
						MakeReservation(order.TargetActor);

						Action enter = () =>
						{
							var exit = order.TargetActor.Info.TraitInfos<ExitInfo>().FirstOrDefault();
							var offset = (exit != null) ? exit.SpawnOffset : WVec.Zero;

							self.QueueActivity(new HeliFly(self, Target.FromPos(order.TargetActor.CenterPosition + offset)));
							self.QueueActivity(new Turn(self, Info.InitialFacing));
							self.QueueActivity(new HeliLand(self, false));
							self.QueueActivity(new ResupplyAircraft(self));
							self.QueueActivity(new TakeOff(self));
						};

						self.QueueActivity(order.Queued, new CallFunc(enter));
					}
				}
			}
			else if (order.OrderString == "Stop")
			{
				self.CancelActivity();
				if (GetActorBelow() != null)
				{
					self.QueueActivity(new ResupplyAircraft(self));
					return;
				}

				UnReserve();

				// TODO: Implement INotifyBecomingIdle instead
				if (!IsPlane && Info.LandWhenIdle)
				{
					if (Info.TurnToLand)
						self.QueueActivity(new Turn(self, Info.InitialFacing));

					self.QueueActivity(new HeliLand(self, true));
				}
			}
			else if (order.OrderString == "ReturnToBase")
			{
				UnReserve();
				self.CancelActivity();
				if (IsPlane)
					self.QueueActivity(new ReturnToBase(self, Info.AbortOnResupply, null, false));
				else
					self.QueueActivity(new HeliReturnToBase(self, Info.AbortOnResupply, null, false));
			}
		}

		#endregion

		public void RemovedFromWorld(Actor self)
		{
			UnReserve();
			self.World.RemoveFromMaps(self, this);

			OnCruisingAltitudeLeft();
			OnAirborneAltitudeLeft();
		}

		#region Airborne conditions

		void OnAirborneAltitudeReached()
		{
			if (airborne)
				return;

			airborne = true;
			if (conditionManager != null && !string.IsNullOrEmpty(Info.AirborneCondition) && airborneToken == ConditionManager.InvalidConditionToken)
				airborneToken = conditionManager.GrantCondition(self, Info.AirborneCondition);
		}

		void OnAirborneAltitudeLeft()
		{
			if (!airborne)
				return;

			airborne = false;
			if (conditionManager != null && airborneToken != ConditionManager.InvalidConditionToken)
				airborneToken = conditionManager.RevokeCondition(self, airborneToken);
		}

		#endregion

		#region Cruising conditions

		void OnCruisingAltitudeReached()
		{
			if (cruising)
				return;

			cruising = true;
			if (conditionManager != null && !string.IsNullOrEmpty(Info.CruisingCondition) && cruisingToken == ConditionManager.InvalidConditionToken)
				cruisingToken = conditionManager.GrantCondition(self, Info.CruisingCondition);
		}

		void OnCruisingAltitudeLeft()
		{
			if (!cruising)
				return;
			cruising = false;
			if (conditionManager != null && cruisingToken != ConditionManager.InvalidConditionToken)
				cruisingToken = conditionManager.RevokeCondition(self, cruisingToken);
		}

		#endregion

		public void Disposing(Actor self)
		{
			UnReserve();
		}

		void IActorPreviewInitModifier.ModifyActorPreviewInit(Actor self, TypeDictionary inits)
		{
			if (!inits.Contains<DynamicFacingInit>() && !inits.Contains<FacingInit>())
				inits.Add(new DynamicFacingInit(() => Facing));
		}
	}
}
