/*
 Navicat Premium Dump SQL

 Source Server         : localhost_3307
 Source Server Type    : MySQL
 Source Server Version : 80036 (8.0.36)
 Source Host           : localhost:3307
 Source Schema         : automatic_online_host

 Target Server Type    : MySQL
 Target Server Version : 80036 (8.0.36)
 File Encoding         : 65001

 Date: 13/05/2026 18:25:03
*/

SET NAMES utf8mb4;
SET FOREIGN_KEY_CHECKS = 0;

-- ----------------------------
-- Table structure for controller
-- ----------------------------
DROP TABLE IF EXISTS `controller`;
CREATE TABLE `controller`  (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `name` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `type_name` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `ip` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `port` int NULL DEFAULT NULL,
  `device_no` int NULL DEFAULT NULL,
  `state` int NULL DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`) USING BTREE
) ENGINE = InnoDB AUTO_INCREMENT = 19 CHARACTER SET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci ROW_FORMAT = Dynamic;

-- ----------------------------
-- Records of controller
-- ----------------------------
INSERT INTO `controller` VALUES (1, '西门子plc研磨机屏幕', '屏幕', '192.168.2.94', 0, 0, 1, '2026-05-12 00:34:01');
INSERT INTO `controller` VALUES (2, '西门子plc研磨机屏幕', '屏幕', '192.168.2.95', 0, 0, 1, '2026-05-12 00:34:01');

-- ----------------------------
-- Table structure for crane
-- ----------------------------
DROP TABLE IF EXISTS `crane`;
CREATE TABLE `crane`  (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `line_no` tinyint NULL DEFAULT NULL,
  `crane_no` int NULL DEFAULT NULL,
  `name` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `ip` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `port` int NULL DEFAULT NULL,
  `encoder_ip` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `encoder_port` int NULL DEFAULT NULL,
  `encoder_no` int NULL DEFAULT NULL,
  `origin_x` bigint NULL DEFAULT NULL,
  `origin_y` bigint NULL DEFAULT NULL,
  `origin_z` bigint NULL DEFAULT NULL,
  `width` bigint NULL DEFAULT NULL,
  `abs_x_offset` int NULL DEFAULT NULL,
  `ratio_x` double NULL DEFAULT NULL,
  `ratio_y` double NULL DEFAULT NULL,
  `ratio_z` double NULL DEFAULT NULL,
  `start_x` bigint NULL DEFAULT NULL,
  `end_x` bigint NULL DEFAULT NULL,
  `limit_zp` bigint NULL DEFAULT NULL,
  `limit_zn` bigint NULL DEFAULT NULL,
  `pulse_x` double NULL DEFAULT NULL,
  `work_sta` int NULL DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `current_x` bigint NULL DEFAULT NULL COMMENT '当前X坐标(PLC实时值)',
  `current_y` bigint NULL DEFAULT NULL COMMENT '当前Y坐标(PLC实时值)',
  `current_z` bigint NULL DEFAULT NULL COMMENT '当前Z坐标(PLC实时值)',
  PRIMARY KEY (`id`) USING BTREE
) ENGINE = InnoDB AUTO_INCREMENT = 16 CHARACTER SET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci ROW_FORMAT = Dynamic;

-- ----------------------------
-- Records of crane
-- ----------------------------
INSERT INTO `crane` VALUES (1, 1, 1, '一号线天车前', '192.168.2.81', 502, '192.168.21.11', 6002, 1, 0, 0, 0, 1200, 0, 1, 1, 1, 0, 100000, 50000, -50000, 1000, 1, '2026-04-27 05:31:57', 11, 123, 111);
INSERT INTO `crane` VALUES (2, 1, 2, '一号线天车后', '192.168.2.82', 502, '192.168.21.12', 6002, 2, 0, 0, 0, 1200, 0, 1, 1, 1, 0, 100000, 50000, -50000, 1000, 1, '2026-04-27 05:31:57', 1, 1, 1);
INSERT INTO `crane` VALUES (3, 1, 3, '二号线天车前', '192.168.2.83', 502, '192.168.21.13', 6002, 3, 0, 0, 0, 1200, 0, 1, 1, 1, 0, 100000, 50000, -50000, 1000, 1, '2026-04-27 05:31:57', 0, 0, 0);
INSERT INTO `crane` VALUES (4, 2, 0, '二号线天车后', '192.168.2.84', 502, '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '2026-04-30 06:26:42', NULL, NULL, NULL);
INSERT INTO `crane` VALUES (5, 1, 0, '研磨机天车', '192.168.2.80', 502, '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '2026-04-30 06:26:42', NULL, NULL, NULL);

-- ----------------------------
-- Table structure for machine
-- ----------------------------
DROP TABLE IF EXISTS `machine`;
CREATE TABLE `machine`  (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `line_no` tinyint NULL DEFAULT NULL,
  `station_code` varchar(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `name` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `type_name` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `area_name` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `machine_no` int NULL DEFAULT NULL,
  `ip` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `port` int NULL DEFAULT NULL,
  `x` int NULL DEFAULT NULL,
  `y` int NULL DEFAULT NULL,
  `z` int NULL DEFAULT NULL,
  `safe_z_down` int NULL DEFAULT NULL,
  `safe_z_up` int NULL DEFAULT NULL,
  `absolute_pos` int NULL DEFAULT NULL,
  `x_dis` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `y_dis` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `z_dis` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `dis_shake` int NULL DEFAULT NULL,
  `process_range` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `state` int NULL DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `current_x` int NULL DEFAULT NULL COMMENT '当前X坐标(PLC实时值)',
  `current_y` int NULL DEFAULT NULL COMMENT '当前Y坐标(PLC实时值)',
  `current_z` int NULL DEFAULT NULL COMMENT '当前Z坐标(PLC实时值)',
  PRIMARY KEY (`id`) USING BTREE,
  INDEX `idx_machine_line`(`line_no` ASC) USING BTREE,
  INDEX `idx_machine_code`(`station_code` ASC) USING BTREE
) ENGINE = InnoDB AUTO_INCREMENT = 81 CHARACTER SET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci ROW_FORMAT = Dynamic;

-- ----------------------------
-- Records of machine
-- ----------------------------
INSERT INTO `machine` VALUES (44, 1, '', '一号线双头镗', '双头镗', '', 0, '192.168.1.401', 0, 0, 0, 0, 0, 0, 0, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', 1, 11, 1);
INSERT INTO `machine` VALUES (45, 1, 'ST501', '一号线打号机', '打号机', NULL, 501, '192.168.1.501', NULL, 1, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (46, 1, 'ST601', '一号线斜床1', '斜床', NULL, 601, '192.168.1.601', NULL, 1, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (47, 1, 'ST602', '一号线斜床2', '斜床', NULL, 602, '192.168.1.602', NULL, 1, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (48, 1, 'ST603', '一号线斜床3', '斜床', NULL, 603, '192.168.1.603', NULL, 1, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (49, 1, 'ST604', '一号线斜床4', '斜床', NULL, 604, '192.168.1.604', NULL, 1, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (50, 1, 'ST605', '一号线斜床5', '斜床', NULL, 605, '192.168.1.605', NULL, 1, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (51, 2, 'ST402', '二号线双头镗', '双头镗', NULL, 402, '192.168.1.402', NULL, 2, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (52, 2, 'ST502', '二号线打号机', '打号机', NULL, 502, '192.168.1.502', NULL, 2, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (53, 2, 'ST606', '二号线斜床6', '斜床', NULL, 606, '192.168.1.606', NULL, 2, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (54, 2, 'ST607', '二号线斜床7', '斜床', NULL, 607, '192.168.1.607', NULL, 2, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (55, 2, 'ST608', '二号线斜床8', '斜床', NULL, 608, '192.168.1.608', NULL, 2, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (56, 2, 'ST609', '二号线斜床9', '斜床', NULL, 609, '192.168.1.609', NULL, 2, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (57, 2, 'ST610', '二号线斜床10', '斜床', NULL, 610, '192.168.1.610', NULL, 2, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (58, 3, 'ST701', '新代数控研磨机1', '研磨机', NULL, 701, '192.168.2.90', NULL, 3, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (59, 1, 'ST702', '新代数控研磨机2', '研磨机', '', 0, '192.168.2.91', 0, 3, 0, 0, 0, 0, 0, '0', '0', '0', 0, '1-100', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (60, 3, 'ST703', '西门子plc研磨机1', '研磨机', NULL, 703, '192.168.2.92', NULL, 3, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (61, 3, 'ST704', '西门子plc研磨机2', '研磨机', NULL, 704, '192.168.2.93', NULL, 3, 0, 0, NULL, NULL, NULL, '0', '0', '0', 0, '', 1, '2026-04-27 05:31:57', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (66, 1, 'ST709', '研磨机上料架', '上料架', '', 0, '192.168.1.308', 0, 0, 0, 0, 0, 0, 0, '0', '0', '0', 0, '0', 1, '2026-04-27 09:33:50', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (67, 2, 'ST710', '研磨机下料架', '下料架', '', 0, '192.168.1.309', 0, 0, 0, 0, 1, 0, 0, '0', '0', '0', 0, '', 1, '2026-04-27 09:35:05', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (68, 1, '', '机械手1', '斜床', '', 0, '192.168.2.85', 0, 1, 1, 1, 0, 0, 0, '0', '0', '0', 0, '', 1, '2026-05-07 01:43:21', 0, 0, 0);
INSERT INTO `machine` VALUES (69, NULL, NULL, '机械手2', '机械手', NULL, NULL, '192.168.2.86', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-05-07 02:17:59', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (70, 1, '', '机械手3', '机械手', '', 0, '192.168.2.87', 0, 0, 0, 0, 0, 0, 0, '0', '0', '0', 0, '', 1, '2026-05-07 02:17:59', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (71, NULL, NULL, '一号线货叉(上料架1)', '货叉 上料架', NULL, NULL, '192.168.2.88', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-05-07 03:28:53', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (72, 1, '', '二号线货叉(上料架2)', '货叉 上料架', '', 0, '192.168.2.89', 0, 1, 0, 0, 0, 0, 0, '0', '0', '0', 0, '', 1, '2026-05-07 06:30:20', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (73, NULL, 'ST901', '1号线天车前', '天车', NULL, NULL, '192.168.2.81', 502, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-05-13 06:39:14', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (74, NULL, 'ST902', '1号线天车后', '天车', NULL, NULL, '192.168.2.82', 502, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-05-13 06:39:14', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (75, NULL, 'ST903', '2号线天车前', '天车', NULL, NULL, '192.168.2.83', 502, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-05-13 06:39:14', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (76, NULL, 'ST904', '2号线天车后', '天车', NULL, NULL, '192.168.2.84', 502, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-05-13 06:39:14', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (77, NULL, 'ST905', '研磨机天车', '天车', NULL, NULL, '192.168.2.80', 502, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-05-13 06:39:14', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (78, NULL, 'ST002', '机械手1', '机械手', NULL, NULL, '192.168.2.85', 502, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-05-13 06:39:14', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (79, NULL, 'ST005', '机械手2', '机械手', NULL, NULL, '192.168.2.86', 502, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-05-13 06:39:14', NULL, NULL, NULL);
INSERT INTO `machine` VALUES (80, NULL, 'ST006', '机械手3', '机械手', NULL, NULL, '192.168.2.87', 502, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-05-13 06:39:14', NULL, NULL, NULL);

-- ----------------------------
-- Table structure for process_route
-- ----------------------------
DROP TABLE IF EXISTS `process_route`;
CREATE TABLE `process_route`  (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `process_name` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `step_no` int NULL DEFAULT NULL,
  `step_name` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `execute_time` int NULL DEFAULT NULL,
  `device` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `enabled` tinyint(1) NULL DEFAULT NULL,
  `z_axis_back_home` tinyint(1) NULL DEFAULT NULL,
  `safe_position` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL DEFAULT NULL,
  `machine_no` int NULL DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`) USING BTREE
) ENGINE = InnoDB AUTO_INCREMENT = 26 CHARACTER SET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci ROW_FORMAT = Dynamic;

-- ----------------------------
-- Records of process_route
-- ----------------------------
INSERT INTO `process_route` VALUES (12, '双头镗标准工艺', 1, '上料定位', 8, '天车', 1, 1, '安全位A', 401, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (13, '双头镗标准工艺', 2, '双头镗加工', 60, '双头镗', 1, 0, '加工位1', 401, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (14, '双头镗标准工艺', 3, '下料转序', 10, '天车', 1, 1, '安全位A', 401, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (15, '打号标准工艺', 1, '夹紧工件', 6, '打号机', 1, 1, '安全位B', 501, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (16, '打号标准工艺', 2, '执行打号', 25, '打号机', 1, 0, '打号位1', 501, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (17, '打号标准工艺', 3, '松夹出站', 6, '打号机', 1, 1, '安全位B', 501, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (18, '斜床标准工艺', 1, '上料', 8, '天车', 1, 1, '安全位C', 601, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (19, '斜床标准工艺', 2, '粗加工', 45, '斜床', 1, 0, '加工位2', 601, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (20, '斜床标准工艺', 3, '精加工', 35, '斜床', 1, 0, '加工位2', 601, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (21, '斜床标准工艺', 4, '下料', 8, '天车', 1, 1, '安全位C', 601, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (22, '研磨标准工艺', 1, '上料', 8, '天车', 1, 1, '安全位D', 701, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (23, '研磨标准工艺', 2, '粗磨', 50, '研磨机', 1, 0, '研磨位1', 701, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (24, '研磨标准工艺', 3, '精磨', 40, '研磨机', 1, 0, '研磨位1', 701, '2026-04-27 05:31:57');
INSERT INTO `process_route` VALUES (25, '研磨标准工艺', 4, '下料', 8, '天车', 1, 1, '安全位D', 701, '2026-04-27 05:31:57');

-- ----------------------------
-- Table structure for workpiece_track
-- ----------------------------
DROP TABLE IF EXISTS `workpiece_track`;
CREATE TABLE `workpiece_track`  (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `plate_no` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL DEFAULT '' COMMENT '版号',
  `sequence` int NULL DEFAULT 0 COMMENT '序号',
  `current_stage` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL DEFAULT 'Idle' COMMENT '当前阶段(Idle/Loading/Boring/Marking/SkewBed/BalanceCheck/Grinding/Done)',
  `process_type` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL DEFAULT '总工艺' COMMENT '工艺类型',
  `length` double NULL DEFAULT 0 COMMENT '版长(mm)',
  `diameter` double NULL DEFAULT 0 COMMENT '外径(mm)',
  `plug_hole` double NULL DEFAULT 0 COMMENT '堵孔(mm)',
  `marking_content` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL DEFAULT '' COMMENT '刻印内容',
  `assigned_line` int NULL DEFAULT NULL COMMENT '分配线体(1或2)',
  `load_method` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL DEFAULT NULL COMMENT '上料方式(天车/货叉)',
  `needs_balance` tinyint(1) NULL DEFAULT 0 COMMENT '是否需动平衡',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  `completed_at` datetime NULL DEFAULT NULL COMMENT '完成时间',
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '更新时间',
  PRIMARY KEY (`id`) USING BTREE,
  INDEX `idx_plate_no`(`plate_no` ASC) USING BTREE,
  INDEX `idx_current_stage`(`current_stage` ASC) USING BTREE,
  INDEX `idx_created_at`(`created_at` ASC) USING BTREE
) ENGINE = InnoDB AUTO_INCREMENT = 1 CHARACTER SET = utf8mb4 COLLATE = utf8mb4_unicode_ci COMMENT = '工件工序跟踪' ROW_FORMAT = Dynamic;

-- ----------------------------
-- Records of workpiece_track
-- ----------------------------

SET FOREIGN_KEY_CHECKS = 1;
