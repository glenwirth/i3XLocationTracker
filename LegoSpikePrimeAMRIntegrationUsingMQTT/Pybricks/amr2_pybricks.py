from pybricks.hubs import PrimeHub
from pybricks.pupdevices import Motor, ColorSensor, ForceSensor, UltrasonicSensor
from pybricks.parameters import Port, Color, Icon
from pybricks.iodevices import UARTDevice
from pybricks.tools import wait, StopWatch
from umath import pi, cos, sin, radians

import ujson

def startbackpack():
    # replaces hub.port.A.mode(MODE_FULL_DUPLEX) + baud(115200)
    ser = UARTDevice(Port.A, baudrate=115200)
    wait(100)
    return ser

def clean(text):
    # cleans text to send out
    # (the SPIKE 2 version also replaced quotes with "\'" and '\"', but those
    # are identical to the originals in Python, so they never changed anything)
    text = text.replace("\r\n", "\\r\\n")
    text = text.replace("\r", "\\r")
    text = text.replace('\n','\\n')
    return text

def EOL(response):
    E = '===' in response
    P = '>>>' in response
    I = '...' in response
    return (E or P or I)

def read():
    #reads the UART buffer until empty
    reply = b'sss'
    response = ''
    while (len(reply) > 0):
        wait(10)
        reply = bp.read_all()
        if reply:
            try:
                response += str(reply, 'utf-8')  # Pybricks bytes have no .decode()
            except:
                response += 'ERR: ' + str(reply)
    return response

def read_wait(t0):
    response = ''
    start = clock.time()
    while (clock.time()-start < t0):
        response += read()
        if EOL(response):
            return response
        wait(10)
    return response

def upload(text): #changed from 20 to 15
    #uploads string in chunks
    reply = ''
    n = 15
    lines = [text[i:i+n] for i in range(0, len(text), n)]
    for line in lines:
        bp.write(bytes(line, 'utf-8'))  # Pybricks strings have no .encode()
        reply += read()
    """if EOL(reply):
        reply += read_wait(1000)"""
    return reply

def ask(text):
    text = text.split('\r\n')[0]#assume single line
    response = upload(clean(text)+'\r\n')
    response = response.replace('>>>','')
    return response

#objects
prhub = PrimeHub()
left_motor = Motor(Port.C)
right_motor = Motor(Port.E)
color_sensor = ColorSensor(Port.D)
#distance_sensor = UltrasonicSensor(Port.F)
force_sensor = ForceSensor(Port.F)
bp = startbackpack()
clock = StopWatch()
prev_ticks_left = 0.0
prev_ticks_right = 0.0
new_x = 30.0
new_y = 30.0
angle = 0.0
degrees = 0.0
initial_degrees = 90
d_center = 0.0
max_speed = 20
# replaces MotorPair.set_default_speed()
default_speed = 0

def start_pair(steering):
    # replaces SPIKE 2 MotorPair('C', 'E').start(steering) at default_speed.
    # Steering 50 stops the inner wheel, 100 spins it backwards at full speed.
    # Speed is %, so x10 converts to deg/s. The left motor is mirrored, so it
    # turns backwards to drive forwards (same as SPIKE 2 MotorPair did).
    speed = default_speed * 10
    left = speed
    right = speed
    if steering > 0:
        right = speed * (1 - steering / 50)
    elif steering < 0:
        left = speed * (1 + steering / 50)
    left_motor.run(-left)
    right_motor.run(right)

def stop_pair():
    left_motor.stop()
    right_motor.stop()

def recordXY():
    global default_speed
    strX = '{0:.1f}'.format(new_x)
    strY = '{0:.1f}'.format(new_y)
    msg = {'X':float(strX), 'Y':float(strY)}
    msgStr = ujson.dumps(msg)
    ##print(msgStr)
    result = ask(msgStr)
    ##print(result)
    if (len(result)>0):
        try:
            resultJSON = ujson.loads(result)
            if (resultJSON["Command"] == "STOP"):
                default_speed = 0
                prhub.display.icon(Icon.FALSE)
            if (resultJSON["Command"] == "GO"):
                default_speed = max_speed
                prhub.display.icon(Icon.RIGHT)
            if (resultJSON["Color"] == "RED"):
                prhub.light.on(Color.RED)
            elif (resultJSON["Color"] == "BLUE"):
                prhub.light.on(Color.BLUE)
            else:
                prhub.light.off()
            #updateXY(float(resultJSON["X"]), float(resultJSON["Y"]))
        except Exception as e:
            print("ERROR : "+str(e))

# update the estimated position of the robot using it's wheel encoder readings
def update_odometry():
    R = 2.8
    N = 360.0

    # read the wheel encoder values
    ticks_left = left_motor.angle()
    ticks_right = right_motor.angle()

    global prev_ticks_left
    global prev_ticks_right
    global angle
    global degrees
    global d_center

    # get the difference in ticks since the last iteration
    d_ticks_left = ticks_left - prev_ticks_left
    d_ticks_right = ticks_right - prev_ticks_right

    # estimate the wheel movements
    d_left_wheel = -2*pi*R*( d_ticks_left / N )
    d_right_wheel = 2*pi*R*( d_ticks_right / N )
    d_center = 0.5 * ( d_left_wheel + d_right_wheel )

    # calculate the new pose
    # heading() replaces hub.motion.yaw_pitch_roll()[0]; both count clockwise as positive
    # (Pybricks heading isn't limited to +/-180, so keep it in range like SPIKE 2 yaw)
    yaw = (prhub.imu.heading() + 180) % 360 - 180
    degrees = yaw + initial_degrees
    if (degrees < 0):
        degrees = 360 + degrees
    angle = radians(degrees)
    updateXY(new_x + ((d_center * cos( angle )) / 10 ), new_y + ((d_center * sin( angle )) / 10 ))

    # save the current tick count for the next iteration
    prev_ticks_left = ticks_left
    prev_ticks_right = ticks_right

def line_follow_to_object():
    global default_speed
    object_reached = False
    left_motor.reset_angle(0)
    right_motor.reset_angle(0)
    prhub.imu.reset_heading(0)
    lastRecordT = 0
    default_speed = 0
    print("Start")
    while object_reached==False:
        update_odometry()
        currentT = clock.time()
        if ((currentT - lastRecordT) >= 1000):
            ##print(str(degrees) + "|" + str(sin(angle)) + "|" + str(cos(angle)))
            recordXY()
            lastRecordT = currentT
        '''distance = distance_sensor.distance()  # mm, 2000 if nothing seen
        if distance>40:'''
        if force_sensor.pressed() == False:
            start_pair((color_sensor.reflection()-50)*2)
            ##start_pair(0)
        else:
            object_reached=True
    stop_pair()

def updateXY(x,y):
    global new_x
    global new_y
    new_x = x
    new_y = y

#execute
prhub.display.text('AMR2')
prhub.speaker.beep()
line_follow_to_object()
raise SystemExit
