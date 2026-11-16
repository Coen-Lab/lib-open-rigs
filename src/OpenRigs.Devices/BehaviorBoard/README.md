# Harp Behavior Interface
This module interfaces and configures a [Harp Behavior](https://github.com/harp-tech/device.behavior) device, developed by [HARP Tech](https://harp.tech/).

## Bonsai Workflow
`BehaviorBoard.bonsai` opens one board as a piece ([Building Blocks](../../../docs/articles/building-blocks.md)); the line pieces in this folder drive its lines.

## Properties
The properties of this workflow allow the user to configure the basic device parameters and set the name of shared `Subjects` to either publish or subscribe to as an interface between your broader Bonsai workflow and a behavior board. 
## Device Features
The [behavior board](https://github.com/harp-tech/device.behavior) is a highly capable device in the Harp ecosystem with multiple features and functions.  

- **Digital outputs** (DO0-DO3) with PWM up to 10 kHz  
- **RGB LED and standard LED control** (via flick-lock and screw terminals)  
- **Valve actuation and poke detection** through RJ45 ports  
- **Servo and camera control** (2 each)  
- **Quadrature counter** (on Port 2)  
- **ADC input** (5 V analog)  
- **Clock sync input** and USB serial communication  
For full hardware specs and configuration details, see the [official repository](https://github.com/harp-tech/device.behavior).

### Properties
| **Property Name**       | **Description**                                                           |
|-------------------------|---------------------------------------------------------------------------|
| `Name`                  | The board's name, `BehaviorBoard-<Entry>`: its entry under `behaviorBoard` in rig.yml, its `AsyncSubject` and its log. The port comes from the entry's `portName`. |
| `CommandsSubjectName`   | The subject that sends commands to the board, `<Name>.Commands`.          |
| `DataSubjectName`       | The subject that receives the board's messages, `<Name>.Data`.            |

The line pieces in this folder take the board's `Name` as `Board`: `PulseOutputs`, `RunningWheel` and `ClockFromBoard` also its two subjects, as `BoardCommandsSubjectName` and `BoardDataSubjectName`; `ManualPulses` its command subject; `ZapitTriggerLine` nothing else.
## Dependencies
### Bonsai:

In order to use this module, bonsai must have the following modules installed via the package manager or `Bonsai.config` file.
- [Harp.Behavior](https://www.nuget.org/packages/Harp.Behavior)
