# Adaptive VR Museum

Closed-loop adaptive VR system that dynamically adjusts exhibit content based on real-time behavioral telemetry.

Accepted paper: ACM IUI 2026 Workshop (ShapeXR)

---

## Overview

This project implements a real-time engagement-aware VR system built in Unity for Meta Quest 3.

The system continuously captures behavioral signals including:

- Gaze dwell time  
- Head angular velocity  
- Locomotion speed  
- Text reading duration  

These signals are fused into a composite engagement score and classified into discrete states that drive adaptive content generation via LLM-based prompts.

---

## System Architecture

Behavioral Signals  
→ Temporal Aggregation  
→ Engagement Classification  
→ Adaptive Content Controller  
→ Firebase Telemetry Pipeline  

The system operates at runtime while maintaining stable 72 FPS on standalone VR hardware.

---

## Key Engineering Components

- Real-time signal fusion and smoothing
- State-driven engagement classifier
- Runtime LLM content adaptation
- Firebase-backed behavioral telemetry storage
- VR performance optimization for standalone hardware

---

#

