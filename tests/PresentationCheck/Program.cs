using System;
using System.Reflection;
using MarioTest;
using RoR2;
using UnityEngine;
static class Program {
 static void Check(bool b,string message) { if(!b)throw new Exception(message); }
 static void Main() {
  var component=new MarioPresentation();
  typeof(MarioPresentation).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(component,null);
  Run.Begin();
  var ownMaster=new CharacterMaster{money=17};
  var mario=new CharacterBody{master=ownMaster,healthComponent=new HealthComponent()};
  var user=new LocalUser{currentNetworkUser=new NetworkUser(),cachedMaster=ownMaster,cachedBody=mario,
   cameraRigController=new CameraRigController{sceneCam=new Camera(),targetBody=mario}};
  LocalUserManager.first=user;
  MarioPresentation.Observe(user,mario); MarioDriver.Player=new MarioDriver(); MarioPresentation.Refresh();
  Check(MarioPresentation.Active && Native.created==0,"live Mario keeps his existing presentation");
  var teammate=new CharacterBody{master=new CharacterMaster{money=999},healthComponent=new HealthComponent()};
  MarioDriver.Player=null; user.cachedBody=null; user.cameraRigController.targetBody=teammate;
  MarioPresentation.Refresh();
  Check(MarioPresentation.Active && Native.created==0 && !MarioHudLayout.on && MarioHud.hides>0,
   "spectating a character that isn't a Mario clears the Mario HUD and opens no view");
  // Spectating another player's Mario shows that Mario's HUD, read through his own puppet's view.
  var watchedMario=new CharacterBody{master=new CharacterMaster{money=999},healthComponent=new HealthComponent()};
  watchedMario.components[typeof(MarioPuppet)]=new MarioPuppet{HudHandle=new IntPtr(77)};
  user.cameraRigController.targetBody=watchedMario; MarioPresentation.Refresh();
  Check(MarioLife.health==watchedMario.healthComponent && MarioCoinHud.master==watchedMario.master
   && MarioCoinHud.view==new IntPtr(77) && MarioHudLayout.on && Native.created==0,
   "spectating another player's Mario shows his life and coins through his own view");
  var watchedPuppet=watchedMario.GetComponent<MarioPuppet>();watchedPuppet.HudHandle=IntPtr.Zero;watchedPuppet.HudSim=new IntPtr(88);
  MarioPresentation.Refresh();
  Check(MarioHud.builtin==new IntPtr(88)&&MarioHudLayout.on,"spectating missing-art Mario lost the live primitive tank");
  user.cameraRigController.targetBody=teammate; MarioPresentation.Refresh();
  Check(MarioLife.health==null && !MarioHudLayout.on && Native.created==0,"watching a non-Mario after a Mario takes the HUD down again");
  user.cameraRigController.targetBody=null; MarioPresentation.Refresh();
  Check(Native.created==1 && MarioCoinHud.master==ownMaster,"dead with no one watched: own money through the independent HUD view");
  Check(MarioLife.health==null && MarioLife.retainEmpty,"free spectator camera keeps an empty life meter");
  var drone=new CharacterBody{master=new CharacterMaster{money=23},healthComponent=new HealthComponent()};
  user.cachedBody=drone; user.cachedMaster=drone.master; user.cameraRigController.targetBody=drone;
  user.cameraRigController.sceneCam=new Camera(); MarioPresentation.Refresh();
  Check(MarioPresentation.Active && MarioPresentation.Camera==user.cameraRigController.sceneCam,"Remote Operation follows replacement camera");
  Check(MarioCoinHud.master==drone.master && MarioLife.health==drone.healthComponent && Native.created==1,"drone HUD uses controlled master without another allocation");
  user.cachedBody=mario;user.cachedMaster=ownMaster;MarioDriver.Player=new MarioDriver(); user.cameraRigController.targetBody=mario;
  MarioPresentation.Observe(user,mario);MarioPresentation.Refresh();
  Check(Native.created==1 && Native.freed==0,"Mario respawn reuses run presentation ownership");
  Run.End(); Check(!MarioPresentation.Active && Native.freed==1 && !MarioHudLayout.on,"run end frees HUD and restores layout");
  Run.Begin();MarioDriver.Player=null; user.cameraRigController.targetBody=mario;
  Check(!MarioPresentation.Active,"new run cannot inherit previous Mario ownership");
  MarioPresentation.Observe(user,mario);MarioPresentation.Refresh();
  user.currentNetworkUser=new NetworkUser();MarioPresentation.Refresh();
  Check(!MarioPresentation.Active && Native.freed==2,"different network user cannot inherit effects");
  Console.WriteLine("PASS: live Mario, spectating a Mario and a non-Mario, death, drone/camera replacement, own funds, respawn, run end and user replacement");
 }
}
