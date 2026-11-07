# 2022-12-20
import utime
from umqtt.simple import MQTTClient
import ubinascii
import machine
import network
import esp
import uos
import gc
from wifi_data import wifi_data # import WiFi configuration
from mqtt_data import mqtt_data # import MQTT configuration
esp.osdebug(None)
gc.collect()

# Turn the REPL on at the beginning of boot
uart = machine.UART(0, 115200, timeout = 50)
uos.dupterm(uart, 1)

WIFI_CONFIG = {
    # Configuration Details for the WiFi connection
    "SSID" : wifi_data["SSID"],
    "PASS" : wifi_data["PASS"]
}

MQTT_CONFIG = {
    # Configuration details of the MQTT Broker
    "USER" : mqtt_data["USER"],
    "PASS" : mqtt_data["PASS"],
    "BROKER" : mqtt_data["BROKER"],
    "PORT" : mqtt_data["PORT"],
    "PUB_POSITION" : mqtt_data["POSITION"],
    "PUB_STATUS" : mqtt_data["STATUS"],
    "SUB_COMMAND" : mqtt_data["COMMAND"],
    "SUB_INFO" : mqtt_data["INFO"],
    "CLIENT_ID" : b'esp_8266-' + ubinascii.hexlify(machine.unique_id())
}

station = network.WLAN(network.STA_IF)
station.active(True)
station.connect(WIFI_CONFIG["SSID"], WIFI_CONFIG["PASS"])
connect_counter = 0

print("Waiting to Connect to Wifi...")

while station.isconnected() == False:
    # Retry connecting to WiFi
    if connect_counter > 10:
        connect_counter = 0
        print("Trying Again")
        station.active(True)
        station.connect(WIFI_CONFIG["SSID"], WIFI_CONFIG["PASS"])
    utime.sleep(1)
    connect_counter += 1

print('WiFi Connection successful:', end=" ")
print(station.ifconfig())

